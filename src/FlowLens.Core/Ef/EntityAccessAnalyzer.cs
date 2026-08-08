using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Core.Ef;

/// <param name="Kind">Always <see cref="EdgeKind.Reads"/> or <see cref="EdgeKind.Writes"/>.</param>
/// <param name="Evidence">Human-checkable: the expression and its file:line.</param>
public sealed record EntityAccess(
    string EntityClrTypeName,
    EdgeKind Kind,
    EdgeMechanism Mechanism,
    string Evidence);

/// <param name="RawSqlSites">
/// Places that reach the database without going through the model. No edge is produced for these -
/// identifying the table would mean parsing the SQL string, which the roadmap forbids - but they
/// are reported so the gap is visible rather than absent.
/// </param>
/// <param name="SaveChangesContexts">
/// DbContext types this body commits. Needed beyond the accesses themselves because a SaveChanges
/// also runs that context's interceptors, which write entities this body never mentions.
/// </param>
public sealed record EntityAccessResult(
    IReadOnlyList<EntityAccess> Accesses,
    IReadOnlyList<string> RawSqlSites,
    IReadOnlyList<string> SaveChangesContexts);

/// <summary>
/// Reads a method body and reports which mapped entities it reads and writes.
/// <para>
/// Everything here resolves through the semantic model, never through naming. The load-bearing
/// step is that a DbSet access is recognised by the static TYPE of the expression
/// (<c>DbSet&lt;Order&gt;</c> gives up <c>Order</c> directly), which means the syntactic shape does
/// not matter: <c>DbSet&lt;Order&gt; Orders =&gt; Set&lt;Order&gt;()</c> and
/// <c>DbSet&lt;Order&gt; Orders { get; set; }</c> analyse identically. That also disposes of the
/// concern recorded as L2 about needing to handle expression-bodied accessors.
/// </para>
/// </summary>
public sealed class EntityAccessAnalyzer(EfModelIndex model)
{
    private const string DbSetMetadataName = "Microsoft.EntityFrameworkCore.DbSet`1";
    private const string DbContextMetadataName = "Microsoft.EntityFrameworkCore.DbContext";

    /// <summary>Members that stage or perform a write. Applies to both DbSet and DbContext receivers.</summary>
    private static readonly HashSet<string> WriteMethods = new(StringComparer.Ordinal)
    {
        "Add", "AddAsync", "AddRange", "AddRangeAsync",
        "Update", "UpdateRange",
        "Remove", "RemoveRange",
        "Attach", "AttachRange",
        "ExecuteUpdate", "ExecuteUpdateAsync",
        "ExecuteDelete", "ExecuteDeleteAsync",
    };

    private static readonly HashSet<string> SaveMethods = new(StringComparer.Ordinal)
    {
        "SaveChanges", "SaveChangesAsync",
    };

    /// <summary>Database access that bypasses the model entirely.</summary>
    private static readonly HashSet<string> RawSqlMethods = new(StringComparer.Ordinal)
    {
        "ExecuteSql", "ExecuteSqlAsync", "ExecuteSqlRaw", "ExecuteSqlRawAsync",
        "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync",
        "FromSqlRaw", "FromSqlInterpolated", "SqlQueryRaw", "SqlQuery",
        "CreateCommand",
    };

    public EntityAccessResult Analyze(
        SyntaxNode body,
        SemanticModel semanticModel,
        string solutionDirectory,
        CancellationToken cancellationToken = default)
    {
        var accesses = new List<EntityAccess>();
        var rawSql = new List<string>();
        var consumedByWrite = new HashSet<SyntaxNode>();
        var saveChangesContexts = new List<(string ContextClrTypeName, string Evidence)>();

        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            {
                continue;
            }

            var location = Where(invocation, solutionDirectory);

            if (RawSqlMethods.Contains(method.Name) && IsDatabaseAccess(method))
            {
                rawSql.Add($"{Text(invocation.Expression)} at {location}");
                continue;
            }

            if (SaveMethods.Contains(method.Name) && IsDbContext(method.ContainingType))
            {
                // The RECEIVER's type, not the method's containing type. SaveChangesAsync is
                // declared on DbContext itself, so ContainingType is always "DbContext" and would
                // never match a module's model - the derived context is only on the receiver.
                var receiver = invocation.Expression is MemberAccessExpressionSyntax access
                    ? semanticModel.GetTypeInfo(access.Expression, cancellationToken).Type
                    : method.ContainingType;

                if (FullName(receiver) is { } contextName)
                {
                    saveChangesContexts.Add((contextName, location));
                }

                continue;
            }

            if (!WriteMethods.Contains(method.Name))
            {
                continue;
            }

            if (TryResolveDbSet(invocation.Expression, semanticModel, cancellationToken) is { } dbSet)
            {
                consumedByWrite.Add(dbSet.Expression);
                Record(accesses, dbSet.EntityClrTypeName, EdgeKind.Writes, dbSet.Mechanism,
                    $"{Text(invocation.Expression)} at {location}");

                CollectSetPropertyColumns(invocation, semanticModel, solutionDirectory, accesses, cancellationToken);
                continue;
            }

            // context.Add(entity) - the receiver carries no entity type, so the argument does.
            if (IsDbContext(method.ContainingType) && invocation.ArgumentList.Arguments.Count > 0)
            {
                var argumentType = semanticModel
                    .GetTypeInfo(invocation.ArgumentList.Arguments[0].Expression, cancellationToken).Type;

                if (FullName(argumentType) is { } name && model.IsMappedEntity(name))
                {
                    Record(accesses, name, EdgeKind.Writes, EdgeMechanism.DbSetProperty,
                        $"{Text(invocation.Expression)} at {location}");
                }
            }
        }

        CollectReads(body, semanticModel, solutionDirectory, consumedByWrite, accesses, cancellationToken);

        CollectEntityConstructions(body, semanticModel, solutionDirectory, accesses, cancellationToken);

        CollectSaveChangesOnlyWrites(
            body, semanticModel, solutionDirectory, saveChangesContexts, accesses, cancellationToken);

        return new EntityAccessResult(
            accesses,
            rawSql,
            [.. saveChangesContexts.Select(c => c.ContextClrTypeName).Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Any DbSet-typed expression that was not the target of a write is a read. Broad on purpose:
    /// missing a read is worse than carrying one, and every query shape in the survey - direct
    /// FirstOrDefaultAsync, AsNoTracking chains, Include, SelectMany over a navigation, and query
    /// builders reassigned across several statements - reduces to "the DbSet was mentioned".
    /// </summary>
    private void CollectReads(
        SyntaxNode body,
        SemanticModel semanticModel,
        string solutionDirectory,
        HashSet<SyntaxNode> consumedByWrite,
        List<EntityAccess> accesses,
        CancellationToken cancellationToken)
    {
        foreach (var expression in body.DescendantNodes().OfType<ExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (consumedByWrite.Contains(expression))
            {
                continue;
            }

            // Only look at the expression itself, not its receivers: walking the chain here would
            // report the same DbSet once per link.
            if (EntityOf(semanticModel.GetTypeInfo(expression, cancellationToken).Type) is not { } entity)
            {
                continue;
            }

            // The DbSet<T> declaration inside the DbContext is not a read of anything.
            if (expression.Parent is MemberAccessExpressionSyntax parent && parent.Expression != expression)
            {
                continue;
            }

            Record(accesses, entity, EdgeKind.Reads, MechanismOf(expression, semanticModel, cancellationToken),
                $"{Text(expression)} at {Where(expression, solutionDirectory)}");
        }
    }

    /// <summary>
    /// SECOND-CLASS rule. Constructing a mapped entity is not persisting it, but it is sometimes
    /// the only trace of one.
    /// <para>
    /// The scope is deliberately narrow - only types present in a loaded model, never every
    /// ObjectCreationExpression. That distinction was measured rather than assumed. Of the entity
    /// constructions in this target, all but one are also reachable through a DbSet write or
    /// through their owner; the exception is ProductEmbedding, whose DbSet is declared but never
    /// referenced because every access goes through raw SQL on an NpgsqlDataSource. Without this
    /// rule discovery.product_embeddings would be absent from the graph entirely. With the narrow
    /// filter, DTO and response records - CheckoutResponse, ChargeRequest - still stay out.
    /// </para>
    /// </summary>
    private void CollectEntityConstructions(
        SyntaxNode body,
        SemanticModel semanticModel,
        string solutionDirectory,
        List<EntityAccess> accesses,
        CancellationToken cancellationToken)
    {
        foreach (var creation in body.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var created = semanticModel.GetTypeInfo(creation, cancellationToken).Type;

            // IsAttributableEntity, not IsMappedEntity: constructing a shared value object says
            // nothing about which owner's table it will end up in.
            if (FullName(created) is not { } name || !model.IsAttributableEntity(name))
            {
                continue;
            }

            Record(accesses, name, EdgeKind.Writes, EdgeMechanism.EntityConstruction,
                $"new {ShortName(name)}(...) at {Where(creation, solutionDirectory)}");
        }
    }

    /// <summary>
    /// SECOND-CLASS rule, applied only when nothing better matched. ProductRepository.UpdateAsync
    /// writes Product with no EF mutation call at all - it mutates a tracked entity and calls
    /// SaveChanges - so keying writes purely on Add/Update/Remove would miss it entirely.
    /// <para>
    /// The inference: this method calls SaveChanges on context C, and has a parameter or local
    /// whose type C maps. Structural rather than name-based, but circumstantial - it reads the
    /// change tracker's behaviour off the signature - so the edge is tagged
    /// <see cref="EdgeMechanism.SaveChangesWithEntityParameter"/> and counted separately.
    /// </para>
    /// </summary>
    private void CollectSaveChangesOnlyWrites(
        SyntaxNode body,
        SemanticModel semanticModel,
        string solutionDirectory,
        List<(string ContextClrTypeName, string Evidence)> saveChangesContexts,
        List<EntityAccess> accesses,
        CancellationToken cancellationToken)
    {
        if (saveChangesContexts.Count == 0)
        {
            return;
        }

        var alreadyWritten = accesses
            .Where(a => a.Kind == EdgeKind.Writes)
            .Select(a => a.EntityClrTypeName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in CandidateEntityTypes(body, semanticModel, cancellationToken))
        {
            if (alreadyWritten.Contains(candidate))
            {
                continue;
            }

            foreach (var (contextName, evidence) in saveChangesContexts)
            {
                var mapped = model.FindEntity(candidate)
                    .Any(m => string.Equals(m.ContextClrTypeName, contextName, StringComparison.Ordinal));

                if (!mapped)
                {
                    continue;
                }

                Record(accesses, candidate, EdgeKind.Writes,
                    EdgeMechanism.SaveChangesWithEntityParameter,
                    $"SaveChanges at {evidence} with a tracked {ShortName(candidate)}");
                break;
            }
        }
    }

    /// <summary>Parameter and local types that could be the entity a bare SaveChanges commits.</summary>
    private static IEnumerable<string> CandidateEntityTypes(
        SyntaxNode body,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in body.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(parameter, cancellationToken) is { Type: { } type }
                && FullName(Unwrap(type)) is { } name
                && seen.Add(name))
            {
                yield return name;
            }
        }

        foreach (var declarator in body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(declarator, cancellationToken) is ILocalSymbol local
                && FullName(Unwrap(local.Type)) is { } name
                && seen.Add(name))
            {
                yield return name;
            }
        }
    }

    /// <summary>
    /// ExecuteUpdateAsync names its columns inline: SetProperty(r =&gt; r.ExpiresAtUtc, ...). That is
    /// the one place a bulk update states exactly which properties it touches, so it is worth
    /// reading rather than settling for a table-level write.
    /// </summary>
    private void CollectSetPropertyColumns(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        string solutionDirectory,
        List<EntityAccess> accesses,
        CancellationToken cancellationToken)
    {
        foreach (var inner in invocation.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inner.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "SetProperty" }
                || inner.ArgumentList.Arguments.Count == 0)
            {
                continue;
            }

            if (inner.ArgumentList.Arguments[0].Expression is not SimpleLambdaExpressionSyntax lambda
                || lambda.Body is not MemberAccessExpressionSyntax member)
            {
                continue;
            }

            if (semanticModel.GetSymbolInfo(member, cancellationToken).Symbol is not IPropertySymbol property)
            {
                continue;
            }

            var declaring = FullName(property.ContainingType);
            if (declaring is null)
            {
                continue;
            }

            foreach (var column in model.FindColumns(declaring, property.Name))
            {
                Record(accesses, column.EntityClrTypeName, EdgeKind.Writes,
                    EdgeMechanism.ExecuteUpdateSetProperty,
                    $"SetProperty({property.Name}) -> {column.QualifiedTableName}.{column.Property.ColumnName} " +
                    $"at {Where(inner, solutionDirectory)}");
            }
        }
    }

    private sealed record DbSetAccess(string EntityClrTypeName, EdgeMechanism Mechanism, SyntaxNode Expression);

    /// <summary>
    /// Walks a receiver chain looking for the DbSet it started from.
    /// <para>
    /// Necessary because the DbSet is often not the immediate receiver:
    /// <c>context.Reservations.Where(...).ExecuteDeleteAsync(ct)</c> has <c>.Where(...)</c> directly
    /// beneath the call, and stopping there would find nothing.
    /// </para>
    /// </summary>
    private DbSetAccess? TryResolveDbSet(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var current = expression;
        var crossedInvocation = false;

        while (current is not null)
        {
            if (current is MemberAccessExpressionSyntax access)
            {
                current = access.Expression;
            }
            else if (current is InvocationExpressionSyntax call)
            {
                if (EntityOf(semanticModel.GetTypeInfo(call, cancellationToken).Type) is { } fromCall)
                {
                    return new DbSetAccess(
                        fromCall,
                        Mechanism(call, crossedInvocation, semanticModel, cancellationToken),
                        call);
                }

                crossedInvocation = true;
                current = call.Expression;
                continue;
            }
            else if (current is ParenthesizedExpressionSyntax parenthesized)
            {
                current = parenthesized.Expression;
            }
            else
            {
                current = null;
            }

            if (current is null)
            {
                break;
            }

            if (EntityOf(semanticModel.GetTypeInfo(current, cancellationToken).Type) is { } entity)
            {
                return new DbSetAccess(
                    entity,
                    Mechanism(current, crossedInvocation, semanticModel, cancellationToken),
                    current);
            }
        }

        return null;
    }

    /// <summary>
    /// How the DbSet was named, which is the distinction the mechanism field exists to record.
    /// Set&lt;T&gt;() takes precedence over the chain test: it IS an invocation, so asking "did we
    /// cross a call?" first would label every Set&lt;T&gt;() as a fluent chain.
    /// </summary>
    private static EdgeMechanism Mechanism(
        SyntaxNode node,
        bool crossedInvocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (node is InvocationExpressionSyntax call && IsSetOfT(call, semanticModel, cancellationToken))
        {
            return EdgeMechanism.SetOfT;
        }

        return crossedInvocation ? EdgeMechanism.FluentChainHead : EdgeMechanism.DbSetProperty;
    }

    private EdgeMechanism MechanismOf(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        Mechanism(expression, crossedInvocation: false, semanticModel, cancellationToken);

    private static bool IsSetOfT(
        InvocationExpressionSyntax call,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        semanticModel.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol { Name: "Set" } method
        && IsDbContext(method.ContainingType);

    /// <summary>The mapped entity behind a DbSet&lt;T&gt;-typed expression, or null.</summary>
    private string? EntityOf(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named)
        {
            return null;
        }

        // Metadata name plus namespace, rather than a rendered display string: display formats
        // differ on how they spell the type parameter and would silently stop matching.
        var definition = named.ConstructedFrom;
        if (definition.MetadataName != "DbSet`1"
            || definition.ContainingNamespace?.ToDisplayString() != "Microsoft.EntityFrameworkCore")
        {
            return null;
        }

        var entity = FullName(named.TypeArguments[0]);

        // Only types EF actually maps become nodes. A DbSet of an unmapped type would be a target
        // bug, but it is not FlowLens's job to invent a table for it.
        return entity is not null && model.IsAttributableEntity(entity) ? entity : null;
    }

    private static bool IsDbContext(ITypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == DbContextMetadataName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>context.Database.ExecuteSqlAsync(...) and NpgsqlDataSource/NpgsqlConnection commands.</summary>
    private static bool IsDatabaseAccess(IMethodSymbol method)
    {
        var containing = method.ContainingType?.ToDisplayString() ?? string.Empty;

        return containing.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || containing.StartsWith("Npgsql", StringComparison.Ordinal)
            || containing.StartsWith("System.Data", StringComparison.Ordinal);
    }

    private static void Record(
        List<EntityAccess> accesses,
        string entity,
        EdgeKind kind,
        EdgeMechanism mechanism,
        string evidence)
    {
        if (accesses.Any(a => a.EntityClrTypeName == entity && a.Kind == kind && a.Mechanism == mechanism))
        {
            return;
        }

        accesses.Add(new EntityAccess(entity, kind, mechanism, evidence));
    }

    /// <summary>Strips Task&lt;T&gt;, IEnumerable&lt;T&gt; and nullable wrappers to reach the entity.</summary>
    private static ITypeSymbol Unwrap(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named
        && named.Name is "Task" or "ValueTask" or "IEnumerable" or "List" or "IReadOnlyList" or "Nullable"
            ? named.TypeArguments[0]
            : type;

    private static string? FullName(ITypeSymbol? type) =>
        type is null or IErrorTypeSymbol ? null : NodeId.ForType(type);

    private static string ShortName(string clrTypeName) =>
        clrTypeName[(clrTypeName.LastIndexOf('.') + 1)..];

    private static string Where(SyntaxNode node, string solutionDirectory)
    {
        var (file, line) = SourceLocation.For(node, solutionDirectory);
        return line > 0 ? $"{file}:{line}" : file;
    }

    private static string Text(SyntaxNode node)
    {
        var text = node.ToString().ReplaceLineEndings(" ");
        return text.Length <= 60 ? text : text[..60] + "...";
    }
}
