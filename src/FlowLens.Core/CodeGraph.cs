namespace FlowLens.Core;

/// <summary>
/// Which way a traversal runs. In Core rather than in a caller: the direction is part of what an
/// answer means, and both the CLI and the HTTP API ask the same two questions.
/// </summary>
public enum TraversalDirection
{
    /// <summary>What this node reaches - the impact-analysis question.</summary>
    Forward,

    /// <summary>What reaches this node - the triage question.</summary>
    Backward,
}

/// <param name="MaxDepth">Hops from the start node. Depth 0 is the start node itself.</param>
/// <param name="IncludeUtility">
/// False drops shared-kernel plumbing (Result.Success and friends). Default true: the graph is
/// the record of what the code does, and thinning it is the caller's decision, not the store's.
/// </param>
/// <param name="EdgeKinds">Null means every kind. Use it to ask narrower questions.</param>
public sealed record TraversalQuery(
    int MaxDepth = 20,
    bool IncludeUtility = true,
    IReadOnlySet<EdgeKind>? EdgeKinds = null);

/// <param name="DepthById">Hops from the start node, by first discovery. BFS makes this the shortest path.</param>
public sealed record Subgraph(
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Edge> Edges,
    IReadOnlyDictionary<string, int> DepthById);

/// <summary>
/// The whole graph in memory, with the adjacency indexes traversal needs.
/// <para>
/// A List plus two dictionaries, exactly as the roadmap prescribes - no graph database. The
/// measured shape justifies it: Phase 2's deepest chain is 10 hops and its node counts are in the
/// hundreds, so breadth-first over a dictionary of adjacency lists answers in well under a
/// millisecond. Anything heavier would be infrastructure bought for a problem this codebase does
/// not have.
/// </para>
/// </summary>
public sealed class CodeGraph
{
    private readonly Dictionary<string, Node> _byId;
    private readonly Dictionary<string, List<Edge>> _outgoing;
    private readonly Dictionary<string, List<Edge>> _incoming;

    public CodeGraph(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        Nodes = nodes;
        Edges = edges;

        _byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _outgoing = new Dictionary<string, List<Edge>>(StringComparer.Ordinal);
        _incoming = new Dictionary<string, List<Edge>>(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            Index(_outgoing, edge.FromId, edge);
            Index(_incoming, edge.ToId, edge);
        }
    }

    public IReadOnlyList<Node> Nodes { get; }

    public IReadOnlyList<Edge> Edges { get; }

    public Node? Find(string id) => _byId.GetValueOrDefault(id);

    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>Roadmap signature: what this node can reach.</summary>
    public IReadOnlyList<Node> Forward(string startId, int maxDepth) =>
        ForwardSubgraph(startId, new TraversalQuery(maxDepth)).Nodes;

    /// <summary>Roadmap signature: what can reach this node. Triage runs on this direction.</summary>
    public IReadOnlyList<Node> Backward(string startId, int maxDepth) =>
        BackwardSubgraph(startId, new TraversalQuery(maxDepth)).Nodes;

    public Subgraph ForwardSubgraph(string startId, TraversalQuery query) =>
        Walk(startId, query, _outgoing, followTo: true);

    public Subgraph BackwardSubgraph(string startId, TraversalQuery query) =>
        Walk(startId, query, _incoming, followTo: false);

    /// <summary>
    /// Breadth-first so the recorded depth is the shortest path rather than whichever route the
    /// search happened to take first.
    /// <para>
    /// <b>The utility tag never affects reachability.</b> The walk always crosses the whole graph
    /// and <see cref="TraversalQuery.IncludeUtility"/> is applied to the RESULT. That is what L8
    /// actually decided - tag, do not filter, and let each consumer thin its own answer - but the
    /// first implementation skipped utility nodes mid-traversal, which silently dropped everything
    /// behind them.
    /// </para>
    /// <para>
    /// Measured, not hypothetical: <c>MigrateAndSeedHostedService</c> is a BackgroundService root
    /// that seeds two tables, and it is reached through <c>IDataSeeder.SeedAsync</c>, an interface
    /// declared in Shared and therefore tagged utility. Skipping that one node removed the ROOT
    /// from four backward answers - "who writes catalog.products?" quietly lost an entry point.
    /// Filtering the output cannot do that, so the guarantee is structural rather than a property
    /// this target happens to have.
    /// </para>
    /// </summary>
    private Subgraph Walk(
        string startId,
        TraversalQuery query,
        Dictionary<string, List<Edge>> adjacency,
        bool followTo)
    {
        if (!_byId.TryGetValue(startId, out var start))
        {
            return new Subgraph([], [], new Dictionary<string, int>(StringComparer.Ordinal));
        }

        var depths = new Dictionary<string, int>(StringComparer.Ordinal) { [startId] = 0 };
        var reached = new List<Node> { start };
        var used = new List<Edge>();
        var queue = new Queue<string>();
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var depth = depths[currentId];

            if (depth >= query.MaxDepth || !adjacency.TryGetValue(currentId, out var edges))
            {
                continue;
            }

            foreach (var edge in edges)
            {
                if (query.EdgeKinds is { } kinds && !kinds.Contains(edge.Kind))
                {
                    continue;
                }

                var nextId = followTo ? edge.ToId : edge.FromId;

                if (!_byId.TryGetValue(nextId, out var next))
                {
                    continue;
                }

                used.Add(edge);

                if (!depths.TryAdd(nextId, depth + 1))
                {
                    continue;
                }

                reached.Add(next);
                queue.Enqueue(nextId);
            }
        }

        return query.IncludeUtility
            ? new Subgraph(reached, used, depths)
            : WithoutUtility(startId, reached, used, depths);
    }

    /// <summary>
    /// Drops utility nodes from an already-complete result. Edges that touch one go with them -
    /// an edge whose other end is gone would dangle - so the chain shows a visible gap rather than
    /// a fabricated link. Nothing downstream is lost: those nodes were reached on the full graph
    /// and keep their real depth.
    /// <para>
    /// The start node is never dropped. Asking what <c>Result.Success</c> reaches is a legitimate
    /// question, and answering it with an empty set would be the silent-loss shape again.
    /// </para>
    /// </summary>
    private static Subgraph WithoutUtility(
        string startId,
        List<Node> reached,
        List<Edge> used,
        Dictionary<string, int> depths)
    {
        var dropped = reached
            .Where(n => n.Utility && !string.Equals(n.Id, startId, StringComparison.Ordinal))
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (dropped.Count == 0)
        {
            return new Subgraph(reached, used, depths);
        }

        return new Subgraph(
            [.. reached.Where(n => !dropped.Contains(n.Id))],
            [.. used.Where(e => !dropped.Contains(e.FromId) && !dropped.Contains(e.ToId))],
            depths
                .Where(entry => !dropped.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));
    }

    private static void Index(Dictionary<string, List<Edge>> index, string key, Edge edge)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        list.Add(edge);
    }
}
