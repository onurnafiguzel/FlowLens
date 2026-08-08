using FlowLens.Core.Ef;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Core;

/// <summary>
/// Adds the data layer on top of the call graph: which methods read and write which entities,
/// which tables and columns those entities map to, and which calls leave the process.
/// <para>
/// Runs as a second pass over the methods the walk already reached rather than inside the walker.
/// That keeps the traversal free of any EF dependency and means the data layer is derived only for
/// code that is actually reachable from an entry point - an unreferenced repository contributes no
/// tables, which is the honest answer.
/// </para>
/// </summary>
internal sealed class DataLayerOverlay(
    EfModelIndex model,
    Solution solution,
    IReadOnlyList<Project> projects,
    string solutionDirectory)
{
    private readonly EntityAccessAnalyzer _entityAccess = new(model);
    private readonly PropertyWriteAnalyzer _propertyWrites = new(model);
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModels = [];
    private readonly Dictionary<string, INamedTypeSymbol?> _entitySymbols = new(StringComparer.Ordinal);
    private Dictionary<(string Module, string Table), (string FilePath, int Line)>? _tableLocations;
    private Dictionary<string, IReadOnlyList<EntityAccess>>? _interceptorWrites;

    public async Task ApplyAsync(
        IReadOnlyDictionary<string, IMethodSymbol> reachedMethods,
        IReadOnlyDictionary<string, SyntaxNode> endpointBodies,
        Dictionary<string, Node> nodes,
        List<Edge> edges,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (model.Snapshots.Count > 0)
        {
            _tableLocations = await FindTableLocationsAsync(cancellationToken);
        }

        var seenEdges = edges
            .Select(e => (e.FromId, e.ToId, e.Kind, e.Mechanism))
            .ToHashSet();

        // Endpoint lambdas are bodies too, and some of them talk to the database directly rather
        // than through a handler: the dev endpoints run ExecuteUpdateAsync and AsNoTracking queries
        // inline. Analysing only the methods the walk reached left those endpoints with no data
        // edges at all - they looked like they touched nothing, which is the silent-loss shape this
        // whole layer is meant to avoid.
        var bodies = reachedMethods
            .Select(entry => (
                NodeId: entry.Key,
                Body: entry.Value.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax(cancellationToken))
                    .FirstOrDefault()))
            .Concat(endpointBodies.Select(entry => (NodeId: entry.Key, Body: (SyntaxNode?)entry.Value)));

        foreach (var (nodeId, body) in bodies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (body is null)
            {
                continue;
            }

            var semanticModel = await GetSemanticModelAsync(body.SyntaxTree, cancellationToken);
            if (semanticModel is null)
            {
                continue;
            }

            foreach (var site in ExternalCallDetector.Find(body, semanticModel, solutionDirectory, cancellationToken))
            {
                AddExternalCall(nodeId, site, nodes, edges, seenEdges);
            }

            if (model.Snapshots.Count == 0)
            {
                continue;
            }

            var access = _entityAccess.Analyze(body, semanticModel, solutionDirectory, cancellationToken);

            // Columns this body names outright. Collected so the row-level rule below can stay out
            // of their way: "Status = next at Order.cs:166" is a strictly better claim than "the
            // row was written", and only one edge per column should carry the answer.
            var named = new HashSet<string>(StringComparer.Ordinal);
            var wholeRows = new List<EntityAccess>();

            foreach (var context in access.SaveChangesContexts)
            {
                foreach (var entity in InterceptorWrites(context, cancellationToken))
                {
                    AddEntityAccess(nodeId, entity, nodes, edges, seenEdges, cancellationToken);
                }
            }

            foreach (var entry in access.Accesses)
            {
                AddEntityAccess(nodeId, entry, nodes, edges, seenEdges, cancellationToken);

                if (entry.Mechanism == EdgeMechanism.EntityConstruction)
                {
                    await AddConstructorColumnWritesAsync(
                        nodeId, entry.EntityClrTypeName, nodes, edges, seenEdges, named, cancellationToken);
                }

                if (entry.WritesWholeRow)
                {
                    wholeRows.Add(entry);
                }
            }

            diagnostics.AddRange(access.RawSqlSites.Select(s =>
                $"raw SQL reaches the database outside the model, so no table edge: {s}"));

            var writes = _propertyWrites.Analyze(body, semanticModel, solutionDirectory, cancellationToken);

            foreach (var column in writes.Columns)
            {
                AddColumnWrite(nodeId, column, nodes, edges, seenEdges, named, cancellationToken);
            }

            // Last, so every precisely named column is already known.
            foreach (var entry in wholeRows)
            {
                AddRowColumnWrites(nodeId, entry, nodes, edges, seenEdges, named, cancellationToken);
            }

            diagnostics.AddRange(writes.UnmappedWrites.Select(w =>
                $"property written but not mapped to a column: {w}"));
        }
    }

    /// <summary>
    /// Every remaining column of a row the statement writes in full.
    /// <para>
    /// Column writes are otherwise derived from assignments, which measures an UPDATE correctly and
    /// an INSERT badly: EF's INSERT lists every mapped column whether or not C# named it. The gap is
    /// not random - it is precisely the columns no statement can name. Measured before this rule:
    /// <c>identity.users.Id</c> (initialised on the <c>Entity</c> base type, so absent from every
    /// derived constructor), the shadow keys and foreign keys of owned tables, and
    /// <c>cart.carts.Items</c>. Not one <c>.Id</c> column existed anywhere in the graph.
    /// </para>
    /// <para>
    /// Row versions are excluded: <c>xmin</c> is written by Postgres, not by the INSERT.
    /// </para>
    /// <para>
    /// Columns some assignment already named keep their precise edge and are skipped here, so this
    /// mechanism marks exactly the claims that rest on "the row was written" and nothing more.
    /// </para>
    /// </summary>
    private void AddRowColumnWrites(
        string fromId,
        EntityAccess access,
        Dictionary<string, Node> nodes,
        List<Edge> edges,
        HashSet<(string, string, EdgeKind, EdgeMechanism)> seen,
        HashSet<string> named,
        CancellationToken cancellationToken)
    {
        var symbol = ResolveEntitySymbol(access.EntityClrTypeName, cancellationToken);
        if (symbol is null)
        {
            return;
        }

        var (entityFile, entityLine) = SourceLocation.For(symbol, solutionDirectory);
        if (entityLine <= 0)
        {
            return;
        }

        foreach (var mapping in model.FindEntity(access.EntityClrTypeName))
        {
            if (mapping.Entity.QualifiedTableName is not { } table)
            {
                // Owned types folded into the owner's table or into a JSON column own no row.
                continue;
            }

            foreach (var property in mapping.Entity.Properties)
            {
                if (property.ColumnName is not { } column || property.IsRowVersion)
                {
                    continue;
                }

                var columnId = NodeId.ForColumn(table, column);
                if (!named.Add(columnId))
                {
                    continue;
                }

                // Shadow columns have no C# member, so they fall back to the entity declaration -
                // the file you would open to understand why the column exists. Coarser than a
                // property's own line, but a real location, which is what the invariant requires.
                var (file, line) = LocateMember(symbol, property) ?? (entityFile, entityLine);

                if (!nodes.ContainsKey(columnId))
                {
                    nodes[columnId] = new Node(
                        columnId,
                        NodeKind.Column,
                        $"{table}.{column}",
                        ModuleOfTable(table, mapping),
                        file,
                        line);
                }

                Add(edges, seen, new Edge(
                    fromId, columnId, EdgeKind.Writes,
                    $"whole row written: {access.Evidence}",
                    Mechanism: EdgeMechanism.RowInsert));

                Add(edges, seen, new Edge(
                    columnId, NodeId.ForTable(table), EdgeKind.MapsTo,
                    $"EF Core model: {access.EntityClrTypeName}.{property.Name} -> {table}",
                    Mechanism: EdgeMechanism.EfModelMapping));
            }
        }
    }

    /// <summary>
    /// Where the C# member behind a column is declared, walking base types.
    /// <para>
    /// Walking up matters: <c>Id</c> is declared once on <c>Shared.Kernel.Entity</c> and inherited
    /// by every aggregate, so looking only at the entity itself finds nothing for the one column
    /// every table has. Complex members are tried under the entity's own name first
    /// (<c>Product.Price</c>) and the value object's second (<c>Money.Amount</c>).
    /// </para>
    /// </summary>
    private (string FilePath, int Line)? LocateMember(INamedTypeSymbol entity, EfProperty property)
    {
        foreach (var candidate in new[] { property.ComplexPropertyName, property.Name })
        {
            if (candidate is null)
            {
                continue;
            }

            for (var type = entity; type is not null; type = type.BaseType)
            {
                foreach (var member in type.GetMembers(candidate))
                {
                    if (member is not (IPropertySymbol or IFieldSymbol))
                    {
                        continue;
                    }

                    var (file, line) = SourceLocation.For(member, solutionDirectory);
                    if (line > 0)
                    {
                        return (file, line);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Attributes an entity constructor's column writes to the method that runs it.
    /// <para>
    /// Necessary because the call walker follows invocations, and <c>new Product(...)</c> is not
    /// one - so the constructor never becomes a node and its body would never be analysed. Without
    /// this, <c>Product.Create</c> and <c>Order.Create</c> report no columns at all, even though
    /// creating an aggregate is precisely where most of its columns are first written. Measured:
    /// POST /api/catalog/products reached zero columns before this existed.
    /// </para>
    /// <para>
    /// The writes are recorded against the CALLING method rather than the constructor, because the
    /// constructor is private and unreachable on its own - Product.Create is the honest answer to
    /// "what wrote catalog.products.Sku".
    /// </para>
    /// </summary>
    private async Task AddConstructorColumnWritesAsync(
        string fromId,
        string entityClrTypeName,
        Dictionary<string, Node> nodes,
        List<Edge> edges,
        HashSet<(string, string, EdgeKind, EdgeMechanism)> seen,
        HashSet<string> named,
        CancellationToken cancellationToken)
    {
        var symbol = ResolveEntitySymbol(entityClrTypeName, cancellationToken);
        if (symbol is null)
        {
            return;
        }

        foreach (var constructor in symbol.InstanceConstructors)
        {
            foreach (var reference in constructor.DeclaringSyntaxReferences)
            {
                var declaration = reference.GetSyntax(cancellationToken);

                var semanticModel = await GetSemanticModelAsync(declaration.SyntaxTree, cancellationToken);
                if (semanticModel is null)
                {
                    continue;
                }

                var writes = _propertyWrites.Analyze(
                    declaration, semanticModel, solutionDirectory, cancellationToken);

                foreach (var column in writes.Columns)
                {
                    AddColumnWrite(fromId, column, nodes, edges, seen, named, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Entities that a SaveChanges on this context writes through an interceptor.
    ///
    /// <para>
    /// Without this, checkout does not reach <c>ordering.outbox_messages</c>. The outbox row is
    /// written by <c>DomainEventToOutboxInterceptor</c> during SaveChanges, so no handler mentions
    /// it and no call edge leads to it - yet "which tables does checkout write?" is wrong without
    /// it, and that is the question the whole tool exists to answer.
    /// </para>
    ///
    /// <para>
    /// <b>The assumption, stated:</b> an interceptor is linked to a context through the EF MODEL,
    /// not through DI - if the interceptor writes an entity that exactly one context maps, that is
    /// taken as the context it serves. Reading the DI registration instead would mean interpreting
    /// <c>AddInterceptors</c> inside a generic helper, which is the same configuration-reading that
    /// L11 records as structurally unreliable. The model link is checkable and wrong only if two
    /// contexts map the same entity, in which case nothing is claimed.
    /// </para>
    ///
    /// <para>
    /// It is an over-approximation in one direction: the interceptor writes an outbox row only when
    /// there are domain events to map, so a SaveChanges with none writes nothing. For impact
    /// analysis that bias is the correct one - a table that might be written must appear.
    /// </para>
    /// </summary>
    private IReadOnlyList<EntityAccess> InterceptorWrites(
        string contextClrTypeName,
        CancellationToken cancellationToken)
    {
        _interceptorWrites ??= BuildInterceptorIndex(cancellationToken);

        return _interceptorWrites.GetValueOrDefault(contextClrTypeName, []);
    }

    private Dictionary<string, IReadOnlyList<EntityAccess>> BuildInterceptorIndex(
        CancellationToken cancellationToken)
    {
        var byContext = new Dictionary<string, List<EntityAccess>>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            var compilation = project.GetCompilationAsync(cancellationToken).GetAwaiter().GetResult();

            var interceptorInterface = compilation?.GetTypeByMetadataName(
                "Microsoft.EntityFrameworkCore.Diagnostics.ISaveChangesInterceptor");

            if (compilation is null || interceptorInterface is null)
            {
                continue;
            }

            foreach (var type in compilation.Assembly.GlobalNamespace.AllTypes(cancellationToken))
            {
                if (type.IsAbstract
                    || type.TypeKind != TypeKind.Class
                    || !type.AllInterfaces.Any(i =>
                        SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, interceptorInterface)))
                {
                    continue;
                }

                foreach (var write in WritesOf(type, cancellationToken))
                {
                    // The context is inferred from the model: whichever one maps this entity.
                    // Ambiguity means several contexts map it, and then nothing is claimed.
                    var owners = model.FindEntity(write.EntityClrTypeName);
                    if (owners.Count != 1)
                    {
                        continue;
                    }

                    var context = owners[0].ContextClrTypeName;

                    if (!byContext.TryGetValue(context, out var list))
                    {
                        list = [];
                        byContext[context] = list;
                    }

                    if (!list.Any(a => a.EntityClrTypeName == write.EntityClrTypeName))
                    {
                        list.Add(write with { Mechanism = EdgeMechanism.SaveChangesInterceptor });
                    }
                }
            }
        }

        return byContext.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<EntityAccess>)kv.Value,
            StringComparer.Ordinal);
    }

    private IEnumerable<EntityAccess> WritesOf(INamedTypeSymbol interceptor, CancellationToken cancellationToken)
    {
        foreach (var reference in interceptor.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax(cancellationToken);

            var semanticModel = GetSemanticModelAsync(declaration.SyntaxTree, cancellationToken)
                .GetAwaiter().GetResult();

            if (semanticModel is null)
            {
                continue;
            }

            var result = _entityAccess.Analyze(declaration, semanticModel, solutionDirectory, cancellationToken);

            foreach (var access in result.Accesses.Where(a => a.Kind == EdgeKind.Writes))
            {
                yield return access with
                {
                    Evidence = $"{interceptor.Name} on SaveChanges · {access.Evidence}",
                };
            }
        }
    }

    private void AddEntityAccess(
        string fromId,
        EntityAccess access,
        Dictionary<string, Node> nodes,
        List<Edge> edges,
        HashSet<(string, string, EdgeKind, EdgeMechanism)> seen,
        CancellationToken cancellationToken)
    {
        var mappings = model.FindEntity(access.EntityClrTypeName);
        if (mappings.Count == 0)
        {
            return;
        }

        var entityId = EnsureEntityNode(access.EntityClrTypeName, mappings[0], nodes, cancellationToken);
        if (entityId is null)
        {
            return;
        }

        Add(edges, seen, new Edge(
            fromId, entityId, access.Kind, access.Evidence,
            Ambiguous: mappings.Count > 1,
            Mechanism: access.Mechanism));

        foreach (var mapping in mappings)
        {
            EnsureTableMapping(entityId, mapping, nodes, edges, seen, cancellationToken);
        }
    }

    private void AddColumnWrite(
        string fromId,
        ColumnWrite write,
        Dictionary<string, Node> nodes,
        List<Edge> edges,
        HashSet<(string, string, EdgeKind, EdgeMechanism)> seen,
        HashSet<string> named,
        CancellationToken cancellationToken)
    {
        var mappings = model.FindEntity(write.EntityClrTypeName);
        if (mappings.Count == 0)
        {
            return;
        }

        var entityId = EnsureEntityNode(write.EntityClrTypeName, mappings[0], nodes, cancellationToken);
        if (entityId is null)
        {
            return;
        }

        EnsureTableMapping(entityId, mappings[0], nodes, edges, seen, cancellationToken);

        // A row-level write into an owned collection touches every column at once. Naming one would
        // be a fabrication, so it stays an entity-level write.
        if (write.ColumnName == "*")
        {
            Add(edges, seen, new Edge(
                fromId, entityId, EdgeKind.Writes, write.Evidence, Mechanism: write.Mechanism));
            return;
        }

        var tableId = NodeId.ForTable(write.QualifiedTableName);
        var columnId = NodeId.ForColumn(write.QualifiedTableName, write.ColumnName);

        named.Add(columnId);

        if (!nodes.ContainsKey(columnId))
        {
            nodes[columnId] = new Node(
                columnId,
                NodeKind.Column,
                $"{write.QualifiedTableName}.{write.ColumnName}",
                ModuleOfTable(write.QualifiedTableName, mappings[0]),
                write.DeclarationFilePath,
                write.DeclarationLine);
        }

        Add(edges, seen, new Edge(
            fromId, columnId, EdgeKind.Writes, write.Evidence, Mechanism: write.Mechanism));

        // Column -> Table, and deliberately NOT Table -> Column.
        //
        // The relationship has to be an edge: leaving it implicit in the id string would force
        // every consumer to parse "column:ordering.orders.Status" to learn which table it belongs
        // to, which is exactly the string-handling a graph exists to replace.
        //
        // The direction is the load-bearing part. Table -> Column reads like a mapping but behaves
        // like reachability: a read-only endpoint that reaches a table would then reach every
        // column any writer of that table touches, and GET /api/catalog/products claimed six
        // column writes when that edge existed. Column -> Table has no such effect - forward
        // traversal from a writer reaches the column and then its table, which is already true by
        // another route, while a reader of the table still reaches no columns at all.
        Add(edges, seen, new Edge(
            columnId, tableId, EdgeKind.MapsTo,
            $"EF Core model: {write.EntityClrTypeName}.{write.PropertyName} -> {write.QualifiedTableName}",
            Mechanism: EdgeMechanism.EfModelMapping));
    }

    private void AddExternalCall(
        string fromId,
        ExternalCallSite site,
        Dictionary<string, Node> nodes,
        List<Edge> edges,
        HashSet<(string, string, EdgeKind, EdgeMechanism)> seen)
    {
        var id = NodeId.ForExternalCall(site.CallerClrTypeName);

        if (!nodes.ContainsKey(id))
        {
            nodes[id] = new Node(
                id,
                NodeKind.ExternalCall,
                site.DisplayName,
                ModuleOfClrName(site.CallerClrTypeName),
                site.FilePath,
                site.Line);
        }

        Add(edges, seen, new Edge(
            fromId, id, EdgeKind.Calls, site.Evidence, Mechanism: EdgeMechanism.HttpClientInvocation));
    }

    /// <summary>
    /// Entity nodes are located at the class declaration - the file you would edit to change what
    /// is stored. Types with no source (there should be none) get no node rather than a fabricated
    /// location, which keeps the mandatory filePath+line invariant true by construction.
    /// </summary>
    private string? EnsureEntityNode(
        string clrTypeName,
        EntityMapping mapping,
        Dictionary<string, Node> nodes,
        CancellationToken cancellationToken)
    {
        var id = NodeId.ForEntity(clrTypeName);

        if (nodes.ContainsKey(id))
        {
            return id;
        }

        var symbol = ResolveEntitySymbol(clrTypeName, cancellationToken);
        if (symbol is null)
        {
            return null;
        }

        var (file, line) = SourceLocation.For(symbol, solutionDirectory);
        if (line <= 0)
        {
            return null;
        }

        nodes[id] = new Node(
            id,
            NodeKind.Entity,
            symbol.Name,
            GraphBuilder.ModuleOf(symbol),
            file,
            line);

        return id;
    }

    private void EnsureTableMapping(
        string entityId,
        EntityMapping mapping,
        Dictionary<string, Node> nodes,
        List<Edge> edges,
        HashSet<(string, string, EdgeKind, EdgeMechanism)> seen,
        CancellationToken cancellationToken)
    {
        if (mapping.Entity.QualifiedTableName is not { } table)
        {
            // Owned types folded into their owner's table, or into a JSON column, own no table.
            return;
        }

        var tableId = NodeId.ForTable(table);

        if (!nodes.ContainsKey(tableId))
        {
            var location = LocateTable(table, mapping, cancellationToken);
            if (location is null)
            {
                return;
            }

            nodes[tableId] = new Node(
                tableId,
                NodeKind.Table,
                table,
                ModuleOfTable(table, mapping),
                location.Value.FilePath,
                location.Value.Line);
        }

        Add(edges, seen, new Edge(
            entityId, tableId, EdgeKind.MapsTo,
            $"EF Core model via {ShortName(mapping.ContextClrTypeName)}",
            Mechanism: EdgeMechanism.EfModelMapping));
    }

    /// <summary>
    /// Prefers the ToTable("orders") literal, because that is the line you would change to rename
    /// the table and therefore the honest attribution for it. Falls back to the entity declaration
    /// when the name comes from convention or sits somewhere this scan does not reach.
    /// </summary>
    private (string FilePath, int Line)? LocateTable(
        string qualifiedTable,
        EntityMapping mapping,
        CancellationToken cancellationToken)
    {
        var bare = mapping.Entity.TableName!;
        var module = ModuleOfTable(qualifiedTable, mapping);

        if (_tableLocations is not null
            && _tableLocations.TryGetValue((module, bare), out var located))
        {
            return located;
        }

        var symbol = ResolveEntitySymbol(mapping.Entity.ClrTypeName, cancellationToken);
        if (symbol is null)
        {
            return null;
        }

        var (file, line) = SourceLocation.For(symbol, solutionDirectory);
        return line > 0 ? (file, line) : null;
    }

    /// <summary>
    /// Indexes every ToTable("literal") in the solution by (module, table name).
    /// <para>
    /// The module is part of the key rather than decoration: this target really does declare both
    /// catalog.outbox_messages and ordering.outbox_messages, so the bare literal is ambiguous.
    /// </para>
    /// </summary>
    private async Task<Dictionary<(string, string), (string, int)>> FindTableLocationsAsync(
        CancellationToken cancellationToken)
    {
        var located = new Dictionary<(string, string), (string, int)>();

        foreach (var project in projects)
        {
            var module = ProjectClassifier.Classify(project.FilePath ?? project.Name, project.Name).Module;

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Migrations restate the whole model, so they contain a ToTable for every table.
                // Attributing a table to a generated snapshot would point the reader at a file they
                // must never edit - the name lives in the entity configuration, and that is the
                // line you change to rename the table.
                if (IsGeneratedModelCode(document.FilePath))
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root is null)
                {
                    continue;
                }

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToTable" }
                        || invocation.ArgumentList.Arguments.Count == 0
                        || invocation.ArgumentList.Arguments[0].Expression
                            is not LiteralExpressionSyntax literal
                        || literal.Token.ValueText is not { Length: > 0 } table)
                    {
                        continue;
                    }

                    var (file, line) = SourceLocation.For(invocation, solutionDirectory);
                    if (line > 0)
                    {
                        located.TryAdd((module, table), (file, line));
                    }
                }
            }
        }

        return located;
    }

    /// <summary>
    /// EF's generated model code: migrations, their designers, and the model snapshot. All three
    /// contain a full ToTable inventory, and none of them is where a name is decided.
    /// </summary>
    private static bool IsGeneratedModelCode(string? filePath)
    {
        if (filePath is null)
        {
            return false;
        }

        var normalized = filePath.Replace('\\', '/');

        return normalized.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase);
    }

    private INamedTypeSymbol? ResolveEntitySymbol(string clrTypeName, CancellationToken cancellationToken)
    {
        if (_entitySymbols.TryGetValue(clrTypeName, out var cached))
        {
            return cached;
        }

        INamedTypeSymbol? found = null;

        foreach (var project in projects)
        {
            var compilation = project.GetCompilationAsync(cancellationToken).GetAwaiter().GetResult();

            // Nested types use '+' in metadata names; entity types here are top level, but ask for
            // both spellings so a nested one would still resolve rather than vanish.
            found = compilation?.GetTypeByMetadataName(clrTypeName)
                ?? compilation?.GetTypeByMetadataName(ToMetadataName(clrTypeName));

            if (found is not null && SourceLocation.IsInSource(found))
            {
                break;
            }

            found = null;
        }

        _entitySymbols[clrTypeName] = found;
        return found;
    }

    private async Task<SemanticModel?> GetSemanticModelAsync(SyntaxTree tree, CancellationToken cancellationToken)
    {
        if (_semanticModels.TryGetValue(tree, out var cached))
        {
            return cached;
        }

        var document = solution.GetDocument(tree);
        if (document is null)
        {
            return null;
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel is not null)
        {
            _semanticModels[tree] = semanticModel;
        }

        return semanticModel;
    }

    private static void Add(
        List<Edge> edges,
        HashSet<(string, string, EdgeKind, EdgeMechanism)> seen,
        Edge edge)
    {
        if (seen.Add((edge.FromId, edge.ToId, edge.Kind, edge.Mechanism)))
        {
            edges.Add(edge);
        }
    }

    private static string ModuleOfTable(string qualifiedTable, EntityMapping mapping) =>
        ModuleOfClrName(mapping.Entity.ClrTypeName);

    private static string ModuleOfClrName(string clrTypeName)
    {
        var segments = clrTypeName.Split('.');
        return segments.Length >= 2 ? segments[1] : ProjectClassifier.UnknownModule;
    }

    private static string ShortName(string clrTypeName) =>
        clrTypeName[(clrTypeName.LastIndexOf('.') + 1)..];

    private static string ToMetadataName(string clrTypeName)
    {
        var lastDot = clrTypeName.LastIndexOf('.');
        return lastDot < 0 ? clrTypeName : clrTypeName[..lastDot] + "+" + clrTypeName[(lastDot + 1)..];
    }
}
