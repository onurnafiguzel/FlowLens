namespace FlowLens.Core;

/// <param name="Repeated">
/// The node was already printed higher up. Its subtree is not repeated - that is what keeps a
/// cyclic graph printable as a tree.
/// </param>
public sealed record TraceTreeLine(
    int Indent,
    Node Node,
    EdgeKind? IncomingKind,
    bool Repeated,
    bool AmbiguousEdge,
    string? Evidence);

/// <summary>
/// Turns the node/edge set into a printable tree. Pure data - the CLI owns the formatting, so the
/// shape stays testable.
/// </summary>
public static class TraceReport
{
    public static IReadOnlyList<TraceTreeLine> BuildTree(TraceResult result, string rootId)
    {
        var nodesById = result.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        var outgoing = result.Edges
            .GroupBy(e => e.FromId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var lines = new List<TraceTreeLine>();
        var expanded = new HashSet<string>(StringComparer.Ordinal);

        if (!nodesById.TryGetValue(rootId, out var root))
        {
            return lines;
        }

        Visit(root, indent: 0, incoming: null, ambiguousEdge: false, evidence: null);
        return lines;

        void Visit(Node node, int indent, EdgeKind? incoming, bool ambiguousEdge, string? evidence)
        {
            var repeated = !expanded.Add(node.Id);
            lines.Add(new TraceTreeLine(indent, node, incoming, repeated, ambiguousEdge, evidence));

            if (repeated || !outgoing.TryGetValue(node.Id, out var edges))
            {
                return;
            }

            // Deterministic order so the printed tree does not shuffle between runs.
            foreach (var edge in edges
                .OrderBy(e => e.Kind)
                .ThenBy(e => e.ToId, StringComparer.Ordinal))
            {
                if (nodesById.TryGetValue(edge.ToId, out var child))
                {
                    Visit(child, indent + 1, edge.Kind, edge.Ambiguous, edge.Evidence);
                }
            }
        }
    }

    /// <summary>Node counts per kind, for the summary line.</summary>
    public static IReadOnlyList<(NodeKind Kind, int Count)> CountByKind(TraceResult result) =>
    [
        .. result.Nodes
            .GroupBy(n => n.Kind)
            .Select(g => (Kind: g.Key, Count: g.Count()))
            .OrderBy(x => x.Kind)
    ];
}
