namespace FlowLens.Core.Docs;

/// <summary>
/// How one module is allowed to depend on another. The category is decided by the LAYER the edge
/// lands in, read from the node id's <c>ModularCommerce.&lt;Module&gt;.&lt;Layer&gt;</c> segment -
/// never guessed from a name.
/// </summary>
public enum ModuleEdgeKind
{
    /// <summary>Through the target's Contracts project. The legitimate synchronous shape.</summary>
    Contract,

    /// <summary>PUBLISHES / CONSUMES. Legitimate and the loosest coupling there is.</summary>
    Event,

    /// <summary>
    /// Straight into Application, Infrastructure or Domain, bypassing Contracts. Flagged as a
    /// candidate, never asserted as a violation - the tool applies a rule, the reader judges.
    /// </summary>
    Direct,
}

public sealed record ModuleEdge(
    string From,
    string To,
    ModuleEdgeKind Kind,
    int Count,
    IReadOnlyList<string> Evidence);

/// <param name="SharedEdgeCount">
/// Edges into Shared, aggregated rather than drawn. Measured at 204 of 219 cross-module edges:
/// drawing them makes Shared a hub joined to everything and says nothing, because every module
/// using Result and Error is the design.
/// </param>
public sealed record ModuleGraph(
    IReadOnlyList<string> Modules,
    IReadOnlyList<ModuleEdge> Edges,
    int SharedEdgeCount,
    IReadOnlyList<string> SharedDependents);

public static class ModuleGraphBuilder
{
    private const string SharedModule = "Shared";
    private const string ContractsLayer = "Contracts";

    public static ModuleGraph Build(CodeGraph graph)
    {
        var byId = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var grouped = new Dictionary<(string From, string To, ModuleEdgeKind Kind), List<string>>();
        var sharedDependents = new HashSet<string>(StringComparer.Ordinal);
        var sharedEdges = 0;

        foreach (var edge in graph.Edges)
        {
            if (!byId.TryGetValue(edge.FromId, out var from) || !byId.TryGetValue(edge.ToId, out var to))
            {
                continue;
            }

            if (string.Equals(from.Module, to.Module, StringComparison.Ordinal))
            {
                continue;
            }

            // Everyone depends on Shared by design; the count is reported instead of drawn.
            // The REVERSE is not neutral and is kept: shared infrastructure calling into a module
            // inverts the dependency, and both measured candidates are exactly that shape.
            if (string.Equals(to.Module, SharedModule, StringComparison.Ordinal))
            {
                sharedEdges++;
                sharedDependents.Add(from.Module);
                continue;
            }

            var kind = Classify(edge, to);

            if (kind is null)
            {
                continue;
            }

            var key = (from.Module, to.Module, kind.Value);

            if (!grouped.TryGetValue(key, out var evidence))
            {
                evidence = [];
                grouped[key] = evidence;
            }

            evidence.Add($"{from.DisplayName} -> {to.DisplayName} ({to.Location})");
        }

        var edges = grouped
            .Select(entry => new ModuleEdge(
                entry.Key.From,
                entry.Key.To,
                entry.Key.Kind,
                entry.Value.Count,
                [.. entry.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Kind)
            .ToList();

        var modules = edges
            .SelectMany(e => new[] { e.From, e.To })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new ModuleGraph(
            modules,
            edges,
            sharedEdges,
            [.. sharedDependents.Order(StringComparer.Ordinal)]);
    }

    private static ModuleEdgeKind? Classify(Edge edge, Node target) => edge.Kind switch
    {
        EdgeKind.Publishes or EdgeKind.Consumes => ModuleEdgeKind.Event,
        EdgeKind.Calls => string.Equals(LayerOf(target), ContractsLayer, StringComparison.Ordinal)
            ? ModuleEdgeKind.Contract
            : ModuleEdgeKind.Direct,

        // Data edges cross modules only through an entity a module owns; that is a table
        // relationship, not a code dependency, and drawing it here would confuse the two.
        _ => null,
    };

    /// <summary>
    /// The layer segment of ModularCommerce.&lt;Module&gt;.&lt;Layer&gt;. Prefixed ids (endpoint:,
    /// table:) belong to a module's own surface and never carry a cross-module dependency.
    /// </summary>
    private static string LayerOf(Node node)
    {
        var id = node.Id;

        foreach (var prefix in new[] { NodeId.EndpointPrefix, NodeId.TablePrefix, NodeId.ColumnPrefix })
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                return "Api";
            }
        }

        foreach (var prefix in new[] { NodeId.EventPrefix, NodeId.EntityPrefix, NodeId.ExternalPrefix })
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                id = id[prefix.Length..];
                break;
            }
        }

        var parts = id.Split('.');

        return parts.Length >= 3 && string.Equals(parts[0], "ModularCommerce", StringComparison.Ordinal)
            ? parts[2]
            : "?";
    }
}
