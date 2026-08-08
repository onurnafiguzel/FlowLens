namespace FlowLens.Core;

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

                if (!query.IncludeUtility && next.Utility)
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

        return new Subgraph(reached, used, depths);
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
