using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Core.Ef;

/// <param name="DeclarationFilePath">Where the C# property is declared - the Column node's location.</param>
public sealed record ColumnWrite(
    string QualifiedTableName,
    string ColumnName,
    string EntityClrTypeName,
    string PropertyName,
    EdgeMechanism Mechanism,
    string Evidence,
    string DeclarationFilePath,
    int DeclarationLine);

/// <param name="UnmappedWrites">
/// Members of mapped entities that were assigned but have no column - EF Ignore()d or computed.
/// Reported rather than dropped: "this property has no column" and "we failed to find its column"
/// look identical from the outside, and only one of them is fine.
/// </param>
public sealed record PropertyWriteResult(
    IReadOnlyList<ColumnWrite> Columns,
    IReadOnlyList<string> UnmappedWrites);

/// <summary>
/// Finds the column-level writes in a method body: assignments to properties of mapped entities,
/// and additions to collections of owned entities.
/// <para>
/// Nothing propagates here, and nothing needs to. Order's only state change outside its
/// constructor lives in the private helper <c>TransitionTo</c>, and <c>MarkPaid -&gt; TransitionTo</c>
/// is already an ordinary CALLS edge from Phase 2. So the column write attaches to the method that
/// literally performs it, and reaching it from an endpoint is the traversal's job - which is the
/// point of having built a graph rather than a report.
/// </para>
/// </summary>
public sealed class PropertyWriteAnalyzer(EfModelIndex model)
{
    private static readonly HashSet<string> CollectionAddMethods = new(StringComparer.Ordinal)
    {
        "Add", "AddRange", "Insert",
    };

    public PropertyWriteResult Analyze(
        SyntaxNode body,
        SemanticModel semanticModel,
        string solutionDirectory,
        CancellationToken cancellationToken = default)
    {
        var columns = new List<ColumnWrite>();
        var unmapped = new List<string>();

        foreach (var node in body.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = node switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left,
                PostfixUnaryExpressionSyntax postfix when IsIncrementOrDecrement(postfix.Kind()) =>
                    postfix.Operand,
                PrefixUnaryExpressionSyntax prefix when IsIncrementOrDecrement(prefix.Kind()) =>
                    prefix.Operand,
                _ => null,
            };

            if (target is not null)
            {
                RecordAssignment(target, node, semanticModel, solutionDirectory, columns, unmapped, cancellationToken);
                continue;
            }

            if (node is InvocationExpressionSyntax invocation)
            {
                RecordCollectionAdd(invocation, semanticModel, solutionDirectory, columns, cancellationToken);
            }
        }

        return new PropertyWriteResult(columns, unmapped);
    }

    private void RecordAssignment(
        ExpressionSyntax target,
        SyntaxNode site,
        SemanticModel semanticModel,
        string solutionDirectory,
        List<ColumnWrite> columns,
        List<string> unmapped,
        CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetSymbolInfo(target, cancellationToken).Symbol;

        var (declaringType, name) = symbol switch
        {
            IPropertySymbol property => (property.ContainingType, property.Name),
            IFieldSymbol field => (field.ContainingType, field.Name),
            _ => (null, null),
        };

        if (declaringType is null || name is null || NodeId.ForType(declaringType) is not { } declaring)
        {
            return;
        }

        // A shared value object's own assignment cannot name a table. Money.Amount backs
        // catalog.products.price_amount, payment.payments.Amount AND ordering.order_lines.UnitPrice,
        // so attributing "Amount = value" inside Money's constructor to all three would claim three
        // writes for one. The owner's site - product.Price = money - resolves unambiguously and is
        // recorded there instead.
        var matches = model.FindColumns(declaring, name)
            .Where(m => EfModelIndex.ModuleOf(declaring) == EfModelIndex.ModuleOf(m.EntityClrTypeName))
            .ToList();

        if (matches.Count == 0)
        {
            // Only interesting when the type IS mapped: an assignment to some unrelated class is
            // not a missing column, it is simply not about the database.
            if (model.IsMappedEntity(declaring))
            {
                unmapped.Add($"{ShortName(declaring)}.{name} at {Where(site, solutionDirectory)} (no column)");
            }

            return;
        }

        // An assignment inside the entity's own constructor is how aggregates set their initial
        // state; Order.Create would otherwise appear to write no columns at all.
        var mechanism = IsInsideConstructorOf(site, declaringType, semanticModel, cancellationToken)
            ? EdgeMechanism.EntityConstructorAssignment
            : EdgeMechanism.PropertyAssignment;

        var (declarationFile, declarationLine) = DeclarationOf(symbol!, solutionDirectory);

        if (declarationLine <= 0)
        {
            // No source for the member means no Column node can carry a location, and the roadmap
            // makes filePath+line mandatory. Skip rather than fabricate one.
            return;
        }

        foreach (var match in matches)
        {
            columns.Add(new ColumnWrite(
                match.QualifiedTableName,
                match.Property.ColumnName!,
                match.EntityClrTypeName,
                name,
                mechanism,
                $"{Text(target)} = ... at {Where(site, solutionDirectory)}",
                declarationFile,
                declarationLine));
        }
    }

    /// <summary>
    /// _statusHistory.Add(new OrderStatusChange(...)) writes a row in the owned type's table. The
    /// signal is the collection's element type, which the semantic model gives directly.
    /// </summary>
    private void RecordCollectionAdd(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        string solutionDirectory,
        List<ColumnWrite> columns,
        CancellationToken cancellationToken)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access
            || !CollectionAddMethods.Contains(access.Name.Identifier.Text))
        {
            return;
        }

        var receiverType = semanticModel.GetTypeInfo(access.Expression, cancellationToken).Type;

        if (receiverType is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } collection
            || NodeId.ForType(collection.TypeArguments[0]) is not { } elementType
            || !model.IsAttributableEntity(elementType))
        {
            return;
        }

        foreach (var mapping in model.FindEntity(elementType))
        {
            if (mapping.Entity.QualifiedTableName is not { } table)
            {
                continue;
            }

            var (file, line) = DeclarationOf(collection.TypeArguments[0], solutionDirectory);
            if (line <= 0)
            {
                continue;
            }

            // A row-level write: every column of the owned type is written at once, so this is
            // recorded against the table rather than pretending to name one column.
            columns.Add(new ColumnWrite(
                table,
                ColumnName: "*",
                mapping.Entity.ClrTypeName,
                PropertyName: "*",
                EdgeMechanism.OwnedCollectionAdd,
                $"{Text(invocation.Expression)}(...) at {Where(invocation, solutionDirectory)}",
                file,
                line));
        }
    }

    private static bool IsInsideConstructorOf(
        SyntaxNode site,
        INamedTypeSymbol declaringType,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var constructor = site.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();

        return constructor is not null
            && semanticModel.GetDeclaredSymbol(constructor, cancellationToken) is { } symbol
            && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, declaringType);
    }

    private static (string FilePath, int Line) DeclarationOf(ISymbol symbol, string solutionDirectory) =>
        SourceLocation.For(symbol, solutionDirectory);

    private static bool IsIncrementOrDecrement(SyntaxKind kind) =>
        kind is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression
            or SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression;

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
