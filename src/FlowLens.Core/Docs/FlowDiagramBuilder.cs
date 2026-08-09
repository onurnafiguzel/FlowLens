using FlowLens.Core.Answers;

namespace FlowLens.Core.Docs;

/// <summary>
/// Turns one endpoint's forward traversal into a diagram small enough to read.
/// <para>
/// Measured, not guessed: checkout reaches 192 nodes raw. Four narrowing rules applied in order
/// bring every one of the 25 endpoints to 23 nodes or fewer (average 6). The numbers per rule are
/// in docs/phase-5-notes.md.
/// </para>
/// </summary>
public static class FlowDiagramBuilder
{
    /// <summary>
    /// The layers the roadmap's diagram is made of. Method, Entity and Column are deliberately
    /// absent: Method is intermediate plumbing, Entity moves onto the edge label, and Column would
    /// add 103 boxes to checkout alone - columns belong in the module document's table.
    /// </summary>
    private static readonly HashSet<NodeKind> LayerKinds =
    [
        NodeKind.Endpoint, NodeKind.Handler, NodeKind.Repository,
        NodeKind.Table, NodeKind.Event, NodeKind.ExternalCall,
    ];

    /// <summary>The kinds that make a branch worth drawing: this diagram answers "what data".</summary>
    private static readonly HashSet<NodeKind> DataKinds =
        [NodeKind.Table, NodeKind.Event, NodeKind.ExternalCall];

    public static FlowDiagram Build(CodeGraph graph, string startId, IReadOnlyList<string> diagnostics)
    {
        var full = graph.ForwardSubgraph(startId, new TraversalQuery());
        var answer = AnswerBuilder.Build(
            graph, full, startId, TraversalDirection.Forward, new TraversalQuery(), diagnostics);

        var byId = full.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var diagnosticFiles = DiagnosticFiles(diagnostics);

        bool HasDiagnostic(Node n) =>
            !string.IsNullOrEmpty(n.FilePath) && diagnosticFiles.Contains(n.FilePath);

        // Rule 1+2: utility out, layer kinds only.
        var afterLayer = full.Nodes
            .Where(n => !n.Utility && LayerKinds.Contains(n.Kind))
            .ToDictionary(n => n.Id, StringComparer.Ordinal);

        // Rule 3: an interface whose implementation is also present adds a third box for one call.
        var interfaces = InterfacesWithImplementations(afterLayer.Values);

        var afterInterfaces = afterLayer.Values
            .Where(n => !interfaces.Contains(n.Id))
            .ToDictionary(n => n.Id, StringComparer.Ordinal);

        // Rule 4: branches that reach no data. A node carrying a diagnostic is EXEMPT - for
        // Discovery that node is the only explanation for an otherwise empty diagram.
        var dataless = afterInterfaces.Values
            .Where(n => !string.Equals(n.Id, startId, StringComparison.Ordinal))
            .Where(n => !HasDiagnostic(n))
            .Where(n => !ReachesData(graph, n.Id))
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        var kept = afterInterfaces.Values
            .Where(n => !dataless.Contains(n.Id))
            .ToDictionary(n => n.Id, StringComparer.Ordinal);

        var edges = Contract(graph, full, kept, byId);

        var nodes = kept.Values
            .Select(n => new DiagramNode(
                n.Id, n.Kind, n.DisplayName, n.Module, n.Location, n.Ambiguous, HasDiagnostic(n)))
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .ToList();

        var hidden = new HiddenCounts(
            Intermediate: full.Nodes.Count(n => !n.Utility && !LayerKinds.Contains(n.Kind)),
            Utility: full.Nodes.Count(n => n.Utility),
            Interfaces: interfaces.Count,
            Dataless: dataless.Count);

        return new FlowDiagram(
            nodes.Single(n => string.Equals(n.Id, startId, StringComparison.Ordinal)),
            nodes,
            edges,
            hidden,
            full.Nodes.Count,
            answer.Limitations,
            answer.DataLayer ?? new DataLayerAnswer([], 0));
    }

    /// <summary>
    /// Rebuilds the edges by walking THROUGH the dropped nodes.
    /// <para>
    /// Keeping only edges whose both ends survived is the obvious implementation and it is wrong:
    /// measured, three dev endpoints came out as two boxes and zero edges, because their path runs
    /// Endpoint -&gt; Entity -&gt; Table and Entity is dropped by the layer filter. Floating boxes are
    /// worse than a busy diagram - they assert nothing while looking like an answer.
    /// </para>
    /// <para>
    /// <b>Soundness:</b> every edge produced here is the compression of a real path in the full
    /// graph, because it is only ever created by walking one. Contraction may shorten a path; it
    /// must never invent one. A test asserts that independently rather than trusting this comment.
    /// </para>
    /// </summary>
    private static List<DiagramEdge> Contract(
        CodeGraph graph,
        Subgraph full,
        Dictionary<string, Node> kept,
        Dictionary<string, Node> byId)
    {
        // Sorted adjacency. Without it the walk follows whatever order the edges arrived in, and
        // two orderings of the same graph produce different diagrams - which the byte-identical
        // test caught on six flow pages.
        // Source order first. Before call sites existed the key started at ToId, which is the fully
        // qualified symbol - alphabetical, with the namespace dominating. Measured: that disagreed
        // with the order the calls are written in for 61% of sibling groups, and a reader scanning
        // left to right has no way to tell the agreeing cases from the rest.
        // Edges with no call site (interface -> implementation is DI resolution, not a call) sort
        // last rather than first: an unknown position must not claim to be the earliest.
        var outgoing = full.Edges
            .GroupBy(e => e.FromId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(e => e.FirstCallSite is null)
                    .ThenBy(e => e.FirstCallSite?.FilePath ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(e => e.FirstCallSite?.Line ?? 0)
                    .ThenBy(e => e.FirstCallSite?.Column ?? 0)
                    .ThenBy(e => e.ToId, StringComparer.Ordinal)
                    .ThenBy(e => e.Kind)
                    .ThenBy(e => e.Mechanism)
                    .ThenBy(e => e.Evidence ?? string.Empty, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        var found = new Dictionary<(string From, string To), Candidate>();

        foreach (var from in kept.Keys)
        {
            // Breadth-first from `from`, passing only through nodes that were dropped. The first
            // kept node on each route becomes the far end of one contracted edge.
            var queue = new Queue<(string Id, EdgeKind Strongest, List<string> Through, IReadOnlyList<CallSite> Origin)>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { from };

            foreach (var edge in outgoing.GetValueOrDefault(from, []))
            {
                // The FIRST hop's call site travels the whole route unchanged. A contracted edge
                // stands for "the call written here eventually reaches that node", so the place it
                // is written is the place the reader has to open - not some intermediate hop.
                Advance(edge, edge.Kind, [], edge.CallSites);
            }

            while (queue.Count > 0)
            {
                var (id, strongest, through, origin) = queue.Dequeue();

                foreach (var edge in outgoing.GetValueOrDefault(id, []))
                {
                    Advance(edge, Stronger(strongest, edge.Kind), through, origin);
                }
            }

            void Advance(Edge edge, EdgeKind strongest, List<string> through, IReadOnlyList<CallSite> origin)
            {
                if (!byId.ContainsKey(edge.ToId))
                {
                    return;
                }

                if (kept.ContainsKey(edge.ToId))
                {
                    // Deliberately NOT gated by `seen`: a kept node reachable by two routes must
                    // have both offered to the tie-break, or the winner is decided by whichever the
                    // search saw first - which is an ordering, not a rule.
                    var key = (from, edge.ToId);
                    var candidate = new Candidate(
                        Style(strongest), LabelFor(through, byId), edge.Ambiguous, origin);

                    // Two routes can reach the same node. Which one describes the link is decided
                    // by a TOTAL order, not by which the search happened to see first: ranking on
                    // kind alone left ties broken by edge order, and the same graph in a different
                    // order then emitted different labels. Measured - it broke the byte-identical
                    // test on six flow pages.
                    if (!found.TryGetValue(key, out var existing) || Precedes(candidate, existing))
                    {
                        found[key] = candidate;
                    }

                    return;
                }

                // Dropped nodes are expanded once - they are only a route, not an answer.
                if (seen.Add(edge.ToId))
                {
                    queue.Enqueue((edge.ToId, strongest, [.. through, edge.ToId], origin));
                }
            }
        }

        return
        [
            .. found
                .Where(entry => !string.Equals(entry.Key.From, entry.Key.To, StringComparison.Ordinal))
                .Select(entry => new DiagramEdge(
                    entry.Key.From, entry.Key.To, entry.Value.Kind, entry.Value.Label,
                    entry.Value.Ambiguous, entry.Value.CallSites))
                .OrderBy(e => e.FromId, StringComparer.Ordinal)
                .ThenBy(e => e.CallSite is null)
                .ThenBy(e => e.CallSite?.FilePath ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.CallSite?.Line ?? 0)
                .ThenBy(e => e.CallSite?.Column ?? 0)
                .ThenBy(e => e.ToId, StringComparer.Ordinal),
        ];
    }

    /// <param name="CallSites">The first hop's call sites, carried across the whole contracted route.</param>
    private sealed record Candidate(
        DiagramEdgeKind Kind, string Label, bool Ambiguous, IReadOnlyList<CallSite> CallSites)
    {
        public CallSite? CallSite => CallSites.Count == 0 ? null : CallSites[0];
    }

    /// <summary>
    /// The most specific relationship on a contracted path decides how it reads. Repository ->
    /// Entity -> Table is a WRITE even though its last edge is MAPS_TO; Handler -> Method -> Event
    /// is a PUBLISH even though its first edge is a CALL.
    /// </summary>
    private static EdgeKind Stronger(EdgeKind a, EdgeKind b) => Weight(b) > Weight(a) ? b : a;

    /// <summary>
    /// A total order over candidate descriptions of one link: most specific kind first, then label,
    /// then ambiguity, then the call site. Total is the load-bearing word - a comparison that can
    /// tie leaves the winner to iteration order, and the output stops being reproducible.
    /// <para>
    /// The call site is part of the key, not a passenger: two routes to the same node can start at
    /// different invocations, and leaving that choice to whichever the search saw first would let
    /// discovery order back in through the field that decides where the reader is sent.
    /// </para>
    /// </summary>
    private static bool Precedes(Candidate candidate, Candidate existing)
    {
        if (Rank(candidate.Kind) != Rank(existing.Kind))
        {
            return Rank(candidate.Kind) < Rank(existing.Kind);
        }

        var byLabel = string.CompareOrdinal(candidate.Label, existing.Label);

        if (byLabel != 0)
        {
            return byLabel < 0;
        }

        if (candidate.Ambiguous != existing.Ambiguous)
        {
            return !candidate.Ambiguous;
        }

        return Compare(candidate.CallSite, existing.CallSite) < 0;

        static int Compare(CallSite? left, CallSite? right)
        {
            // A missing call site sorts last: not knowing where a call is written must never read
            // as "it is written first".
            if (left is null || right is null)
            {
                return (left is null ? 1 : 0) - (right is null ? 1 : 0);
            }

            var byFile = string.CompareOrdinal(left.FilePath, right.FilePath);

            return byFile != 0 ? byFile
                : left.Line != right.Line ? left.Line - right.Line
                : left.Column - right.Column;
        }
    }

    private static int Weight(EdgeKind kind) => kind switch
    {
        EdgeKind.Publishes or EdgeKind.Consumes => 4,
        EdgeKind.Writes => 3,
        EdgeKind.Reads => 2,
        EdgeKind.MapsTo => 1,
        _ => 0,
    };

    private static DiagramEdgeKind Style(EdgeKind kind) => kind switch
    {
        EdgeKind.Publishes or EdgeKind.Consumes => DiagramEdgeKind.Async,
        EdgeKind.Writes or EdgeKind.Reads or EdgeKind.MapsTo => DiagramEdgeKind.Data,
        _ => DiagramEdgeKind.Call,
    };

    private static int Rank(DiagramEdgeKind kind) => kind switch
    {
        DiagramEdgeKind.Async => 0,
        DiagramEdgeKind.Data => 1,
        _ => 2,
    };

    /// <summary>
    /// One dropped node worth naming gets named - the entity behind a table write, the interface
    /// behind a call. Several dropped methods get nothing: a list of plumbing is noise on an edge.
    /// </summary>
    private static string LabelFor(List<string> through, Dictionary<string, Node> byId)
    {
        if (through.Count != 1 || !byId.TryGetValue(through[0], out var node))
        {
            return string.Empty;
        }

        return node.Kind is NodeKind.Entity ? ShortName(node.DisplayName) : string.Empty;
    }

    /// <summary>
    /// Interface declarations whose implementation is also on the diagram. Measured on checkout:
    /// ICartRepository.GetAsync, CachingCartRepository.GetAsync and PostgresCartRepository.GetAsync
    /// are three boxes for one call. The implementation is kept because that is where the table is
    /// touched and where file:line is useful.
    /// </summary>
    private static HashSet<string> InterfacesWithImplementations(IEnumerable<Node> nodes)
    {
        var drop = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in nodes.GroupBy(MemberName, StringComparer.Ordinal))
        {
            var declared = group.ToList();
            var isInterface = declared.Where(n => IsInterfaceMember(n.DisplayName)).ToList();

            if (isInterface.Count > 0 && isInterface.Count < declared.Count)
            {
                foreach (var node in isInterface)
                {
                    drop.Add(node.Id);
                }
            }
        }

        return drop;

        static string MemberName(Node node)
        {
            var dot = node.DisplayName.LastIndexOf('.');
            return dot < 0 ? node.DisplayName : node.DisplayName[(dot + 1)..];
        }

        // The C# convention, and the one this target follows without exception: IFoo.Bar.
        static bool IsInterfaceMember(string displayName) =>
            displayName.Length > 1 && displayName[0] == 'I' && char.IsUpper(displayName[1]);
    }

    private static bool ReachesData(CodeGraph graph, string id)
    {
        var node = graph.Find(id);

        return node is not null
            && (DataKinds.Contains(node.Kind)
                || graph.ForwardSubgraph(id, new TraversalQuery()).Nodes.Any(n => DataKinds.Contains(n.Kind)));
    }

    /// <summary>Files a build diagnostic names, so the node standing on one can be kept and marked.</summary>
    private static HashSet<string> DiagnosticFiles(IReadOnlyList<string> diagnostics)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var diagnostic in diagnostics)
        {
            foreach (var token in diagnostic.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var cut = token.LastIndexOf(".cs:", StringComparison.OrdinalIgnoreCase);

                if (cut > 0)
                {
                    files.Add(token[..(cut + 3)]);
                }
            }
        }

        return files;
    }

    private static string ShortName(string value)
    {
        var dot = value.LastIndexOf('.');
        return dot < 0 ? value : value[(dot + 1)..];
    }
}
