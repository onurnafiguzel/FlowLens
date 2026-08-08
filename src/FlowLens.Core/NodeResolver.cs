namespace FlowLens.Core;

/// <summary>
/// Turns whatever a caller typed into a node id.
/// <para>
/// In Core so the CLI and the HTTP API accept exactly the same strings. Node ids carry spaces,
/// slashes, colons, braces and angle brackets - "POST /api/ordering/checkout",
/// "table:ordering.orders", "Consume(ConsumeContext&lt;OrderPaid&gt;)" - and inventing a second,
/// friendlier addressing scheme for HTTP would mean two vocabularies for one graph. Percent
/// encoding carries all of it in a query string; this class handles the rest.
/// </para>
/// </summary>
public static class NodeResolver
{
    /// <summary>
    /// Accepts a node id, a route, or a display name. Returns null when nothing matches - callers
    /// must report that rather than returning an empty result, which reads as "nothing is there".
    /// </summary>
    public static string? Resolve(CodeGraph graph, string selector)
    {
        var trimmed = selector.Trim();

        if (graph.Contains(trimmed))
        {
            return trimmed;
        }

        var asEndpoint = NodeId.EndpointPrefix + Normalize(trimmed);
        if (graph.Contains(asEndpoint))
        {
            return asEndpoint;
        }

        return graph.Nodes
            .FirstOrDefault(n =>
                string.Equals(n.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Normalize(n.DisplayName), Normalize(trimmed), StringComparison.Ordinal))
            ?.Id;
    }

    /// <summary>
    /// Ids worth suggesting when nothing resolved. A near miss is far more useful than "not found",
    /// because the usual cause is a route typed slightly differently.
    /// </summary>
    public static IReadOnlyList<string> NearMatches(CodeGraph graph, string selector, int limit = 10)
    {
        var needle = selector.Trim();

        return
        [
            .. graph.Nodes
                .Where(n => n.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || n.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .Select(n => n.Id),
        ];
    }

    private static string Normalize(string value) =>
        value.Trim().ToUpperInvariant().Replace("  ", " ", StringComparison.Ordinal);
}
