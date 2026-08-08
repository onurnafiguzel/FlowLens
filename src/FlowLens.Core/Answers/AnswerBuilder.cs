namespace FlowLens.Core.Answers;

/// <summary>
/// Turns a traversal into an answer: which tables and columns, read or written, on what evidence,
/// and what the graph knowingly could not see.
/// <para>
/// One implementation, three callers - the CLI, the HTTP API, and later the documentation
/// generator. The read/write derivation used to live in the CLI's printer, which meant the only
/// way to reuse it was to reimplement it, and two implementations of the same rule drift silently.
/// </para>
/// </summary>
public static class AnswerBuilder
{
    private const string ForwardDirection = "forward";
    private const string BackwardDirection = "backward";

    /// <summary>
    /// Mechanisms that read the fact straight out of the code or EF Core's model. Everything not
    /// listed here is a weaker claim, and which weaker kind it is decides the bucket.
    /// </summary>
    private static readonly HashSet<EdgeMechanism> DirectMechanisms =
    [
        EdgeMechanism.DbSetProperty,
        EdgeMechanism.SetOfT,
        EdgeMechanism.FluentChainHead,
        EdgeMechanism.ExecuteUpdateSetProperty,
        EdgeMechanism.PropertyAssignment,
        EdgeMechanism.OwnedCollectionAdd,
        EdgeMechanism.EntityConstructorAssignment,
        EdgeMechanism.EfModelMapping,
    ];

    public static TraceAnswer Build(
        CodeGraph graph,
        Subgraph subgraph,
        string startId,
        TraversalDirection direction,
        TraversalQuery query,
        IReadOnlyList<string> diagnostics)
    {
        var start = graph.Find(startId)!;
        var forward = direction == TraversalDirection.Forward;

        var nodes = subgraph.Nodes
            .Where(n => !string.Equals(n.Id, startId, StringComparison.Ordinal))
            .Select(n => Ref(n, subgraph))
            .OrderBy(n => n.Depth)
            .ThenBy(n => n.DisplayName, StringComparer.Ordinal)
            .ToList();

        var truncated = IsTruncated(graph, subgraph, direction, query);

        return new TraceAnswer(
            Ref(start, subgraph),
            forward ? ForwardDirection : BackwardDirection,
            subgraph.Nodes.Count,
            query.MaxDepth,
            truncated,
            nodes,

            // Forward only, deliberately. Walking backward from a table and then listing "the
            // tables" would answer a question nobody asked - the CLI printed exactly that block
            // under both directions with two different meanings, and it read as one.
            forward ? DataLayer(subgraph) : null,
            forward ? null : EntryPoints(subgraph, startId),

            EventBridges(subgraph),
            new FilteredCounts(CountFiltered(graph, subgraph, startId, query)),
            Limitations(subgraph, diagnostics, truncated, query));
    }

    /// <summary>
    /// Tables reached, each with the access it was reached by and the columns some edge named.
    /// <para>
    /// Public because the CLI renders it for both directions. That is F10 in
    /// docs/known-limitations.md and it is preserved here on purpose: fixing it in the CLI would
    /// change output this phase is verifying byte-for-byte. The API applies the fix instead, by
    /// not carrying the block on a backward answer at all.
    /// </para>
    /// </summary>
    public static DataLayerAnswer DataLayer(Subgraph subgraph)
    {
        var access = AccessByTable(subgraph);
        var columns = ColumnsByTable(subgraph);

        var tables = subgraph.Nodes
            .Where(n => n.Kind == NodeKind.Table)
            .OrderBy(n => n.DisplayName, StringComparer.Ordinal)
            .Select(table => new TableAnswer(
                table.DisplayName,
                SchemaOf(table.DisplayName),
                table.Module,
                table.FilePath,
                table.Line,
                access.GetValueOrDefault(table.Id, string.Empty),
                columns.GetValueOrDefault(table.DisplayName, [])))
            .ToList();

        return new DataLayerAnswer(tables, tables.Sum(t => t.Columns.Count));
    }

    /// <summary>
    /// Whether each table is read, written, or both. Derived from the edges rather than assumed: a
    /// table reached only through a query must not look like a write, and that distinction decides
    /// whether a change to it needs a migration.
    /// </summary>
    private static Dictionary<string, string> AccessByTable(Subgraph subgraph)
    {
        var tablesByEntity = subgraph.Edges
            .Where(e => e.Kind == EdgeKind.MapsTo
                && e.FromId.StartsWith(NodeId.EntityPrefix, StringComparison.Ordinal))
            .GroupBy(e => e.FromId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList(), StringComparer.Ordinal);

        var reads = new HashSet<string>(StringComparer.Ordinal);
        var writes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in subgraph.Edges)
        {
            if (edge.Kind is not (EdgeKind.Reads or EdgeKind.Writes))
            {
                continue;
            }

            var target = edge.Kind == EdgeKind.Writes ? writes : reads;

            if (tablesByEntity.TryGetValue(edge.ToId, out var mapped))
            {
                foreach (var table in mapped)
                {
                    target.Add(table);
                }
            }

            // A direct column write also establishes a write on its table.
            if (edge.ToId.StartsWith(NodeId.ColumnPrefix, StringComparison.Ordinal))
            {
                var id = edge.ToId[NodeId.ColumnPrefix.Length..];
                writes.Add(NodeId.ForTable(id[..id.LastIndexOf('.')]));
            }
        }

        return reads.Union(writes).ToDictionary(
            table => table,
            table => (writes.Contains(table) ? "W" : string.Empty) + (reads.Contains(table) ? "R" : string.Empty),
            StringComparer.Ordinal);
    }

    private static Dictionary<string, IReadOnlyList<ColumnAnswer>> ColumnsByTable(Subgraph subgraph)
    {
        var mechanisms = subgraph.Edges
            .Where(e => e.Kind == EdgeKind.Writes
                && e.ToId.StartsWith(NodeId.ColumnPrefix, StringComparison.Ordinal))
            .GroupBy(e => e.ToId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.Mechanism).Distinct().ToList(),
                StringComparer.Ordinal);

        return subgraph.Nodes
            .Where(n => n.Kind == NodeKind.Column)
            .Select(node =>
            {
                var qualified = node.Id[NodeId.ColumnPrefix.Length..];
                var split = qualified.LastIndexOf('.');
                var used = mechanisms.GetValueOrDefault(node.Id, []);

                return (
                    Table: qualified[..split],
                    Column: new ColumnAnswer(
                        qualified[(split + 1)..],
                        node.FilePath,
                        node.Line,
                        [.. used.Select(m => m.ToString()).Order(StringComparer.Ordinal)],
                        Confidence(used)));
            })
            .GroupBy(x => x.Table, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ColumnAnswer>)
                    [.. g.Select(x => x.Column).OrderBy(c => c.Name, StringComparer.Ordinal)],
                StringComparer.Ordinal);
    }

    /// <summary>
    /// The strongest claim available for a column, not the weakest. A column backed by both an
    /// assignment and a row-level write IS named by an assignment; reporting it as row-level would
    /// understate evidence that exists.
    /// </summary>
    private static ClaimConfidence Confidence(IReadOnlyList<EdgeMechanism> mechanisms)
    {
        if (mechanisms.Any(DirectMechanisms.Contains))
        {
            return ClaimConfidence.Direct;
        }

        if (mechanisms.Contains(EdgeMechanism.RowInsert))
        {
            return ClaimConfidence.RowLevel;
        }

        return mechanisms.Contains(EdgeMechanism.SaveChangesInterceptor)
            ? ClaimConfidence.Inferred
            : ClaimConfidence.SecondClass;
    }

    private static EntryPointsAnswer EntryPoints(Subgraph subgraph, string startId)
    {
        var roots = subgraph.Nodes
            .Where(n => n.RootKind != RootKind.None
                && !string.Equals(n.Id, startId, StringComparison.Ordinal))
            .ToList();

        var groups = roots
            .GroupBy(n => n.RootKind)
            .OrderBy(g => g.Key)
            .Select(g => new EntryPointGroup(
                g.Key,
                g.Count(),
                [
                    .. g.Select(n => Ref(n, subgraph))
                        .OrderBy(n => n.Depth)
                        .ThenBy(n => n.DisplayName, StringComparer.Ordinal),
                ]))
            .ToList();

        return new EntryPointsAnswer(roots.Count, groups);
    }

    private static IReadOnlyList<EventBridgeAnswer> EventBridges(Subgraph subgraph)
    {
        var published = Ends(EdgeKind.Publishes, e => e.ToId, e => e.FromId);
        var consumed = Ends(EdgeKind.Consumes, e => e.FromId, e => e.ToId);

        return
        [
            .. subgraph.Nodes
                .Where(n => n.Kind == NodeKind.Event)
                .OrderBy(n => n.DisplayName, StringComparer.Ordinal)
                .Select(n => new EventBridgeAnswer(
                    n.Id,
                    n.DisplayName,
                    n.Module,
                    n.Location,
                    published.GetValueOrDefault(n.Id, []),
                    consumed.GetValueOrDefault(n.Id, []))),
        ];

        Dictionary<string, IReadOnlyList<string>> Ends(
            EdgeKind kind,
            Func<Edge, string> eventEnd,
            Func<Edge, string> otherEnd) =>
            subgraph.Edges
                .Where(e => e.Kind == kind)
                .GroupBy(eventEnd, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)[.. g.Select(otherEnd).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    StringComparer.Ordinal);
    }

    /// <summary>
    /// How many nodes this answer hides. Reported because a thinned answer and a short answer look
    /// identical otherwise, and telling them apart is the whole point of the utility tag.
    /// </summary>
    private static int CountFiltered(CodeGraph graph, Subgraph subgraph, string startId, TraversalQuery query)
    {
        if (query.IncludeUtility)
        {
            return 0;
        }

        var full = query with { IncludeUtility = true };

        var unfiltered = subgraph.Nodes.Count == 0
            ? 0
            : graph.ForwardSubgraph(startId, full).Nodes.Count;

        return Math.Max(0, unfiltered - subgraph.Nodes.Count);
    }

    /// <summary>
    /// True when the walk stopped at the depth limit with edges still unexplored - not simply when
    /// a node sits at the limit. Reporting the limit as a truncation whenever it was reached would
    /// cry wolf on every answer whose longest chain happens to end there.
    /// </summary>
    private static bool IsTruncated(
        CodeGraph graph,
        Subgraph subgraph,
        TraversalDirection direction,
        TraversalQuery query)
    {
        var frontier = subgraph.Nodes
            .Where(n => subgraph.DepthById.GetValueOrDefault(n.Id) >= query.MaxDepth)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (frontier.Count == 0)
        {
            return false;
        }

        var reached = subgraph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        return graph.Edges.Any(e => direction == TraversalDirection.Forward
            ? frontier.Contains(e.FromId) && !reached.Contains(e.ToId)
            : frontier.Contains(e.ToId) && !reached.Contains(e.FromId));
    }

    /// <summary>
    /// What this answer knowingly leaves out.
    /// <para>
    /// Two sources. Build-time diagnostics are matched to the answer by FILE: a diagnostic carries
    /// file:line, every node carries a file, so a raw-SQL site inside a method this flow reaches is
    /// this flow's limitation. That is what turns "0 tables" into "this flow uses raw SQL at
    /// ProductVectorRepository.cs:60, so its table is not in the list". The rest are read off the
    /// answer itself.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Limitation> Limitations(
        Subgraph subgraph,
        IReadOnlyList<string> diagnostics,
        bool truncated,
        TraversalQuery query)
    {
        var limitations = new List<Limitation>();
        var files = subgraph.Nodes.Select(n => n.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddDiagnostic(
            "raw-sql",
            "raw SQL reaches the database outside the model",
            "Bu akis ham SQL kullaniyor; o erisimin tablosu bu listede YOK.");

        AddDiagnostic(
            "unmapped-column",
            "property written but not mapped to a column",
            "Yazilan bir property'nin kolonu yok (Ignore edilmis, hesaplanan ya da JSON alani).");

        var secondClass = subgraph.Edges
            .Where(e => e.Mechanism is EdgeMechanism.EntityConstruction
                or EdgeMechanism.SaveChangesWithEntityParameter)
            .Select(e => e.Evidence)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (secondClass.Count > 0)
        {
            limitations.Add(new Limitation(
                "second-class-evidence",
                "Bazi yazma iddialari dolayli kanita dayaniyor: entity insasi ya da parametreli bir " +
                "SaveChanges. Dogru olabilir, ama dogrudan okunmus degil.",
                secondClass));
        }

        var ambiguous = subgraph.Nodes
            .Where(n => n.Ambiguous)
            .Select(n => n.Location)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (ambiguous.Count > 0)
        {
            limitations.Add(new Limitation(
                "ambiguous-implementation",
                "Bir interface cagrisi birden fazla implementasyona aciliyor. Graph HANGISININ " +
                "kostugunu KAYDETMIYOR: dekorator zinciri ve koleksiyon enjeksiyonunda hepsi kosar " +
                "(dogru cevap), config anahtariyla secilende yalniz biri kosar (asiri-yaklasim). " +
                "Olculen bedel: veri katmaninda 0 tablo/0 kolon, ExternalCall'da 1/1 yanlis pozitif.",
                ambiguous));
        }

        var interceptorTables = subgraph.Edges
            .Where(e => e.Mechanism == EdgeMechanism.SaveChangesInterceptor)
            .Select(e => e.ToId)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        if (interceptorTables.Count > 0)
        {
            limitations.Add(new Limitation(
                "interceptor-columns",
                "Bir SaveChanges interceptor'inin yazdigi tablolar TABLO duzeyinde dogru, KOLON " +
                "duzeyinde bos: interceptor'i EF cagirir, kod degil, dolayisiyla govdesindeki " +
                "atamalar hicbir akisin ulasilabilir kumesinde degil (known-limitations L16-4).",
                [.. interceptorTables.Order(StringComparer.Ordinal)]));
        }

        if (truncated)
        {
            limitations.Add(new Limitation(
                "truncated",
                $"Yuruyus {query.MaxDepth} derinliginde durdu ve gezilmemis kenar kaldi. " +
                "Daha buyuk bir maxDepth ile tekrar sorun.",
                []));
        }

        return limitations;

        void AddDiagnostic(string code, string marker, string message)
        {
            var hits = diagnostics
                .Where(d => d.Contains(marker, StringComparison.Ordinal) && MentionsReachedFile(d))
                .Order(StringComparer.Ordinal)
                .ToList();

            if (hits.Count > 0)
            {
                limitations.Add(new Limitation(code, message, hits));
            }
        }

        bool MentionsReachedFile(string diagnostic) =>
            files.Any(f => !string.IsNullOrEmpty(f) && diagnostic.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private static NodeRef Ref(Node node, Subgraph subgraph) =>
        new(node.Id, node.Kind, node.RootKind, node.DisplayName, node.Module, node.FilePath,
            node.Line, subgraph.DepthById.GetValueOrDefault(node.Id), node.Ambiguous, node.Utility);

    private static string? SchemaOf(string qualifiedTableName)
    {
        var dot = qualifiedTableName.IndexOf('.');
        return dot > 0 ? qualifiedTableName[..dot] : null;
    }
}
