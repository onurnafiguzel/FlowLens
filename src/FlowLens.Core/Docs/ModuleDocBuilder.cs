using FlowLens.Core.Answers;

namespace FlowLens.Core.Docs;

public sealed record ModuleEndpoint(string Id, string DisplayName, string Location, int TableCount);

public sealed record ModuleTable(string Table, string Access, string Location, IReadOnlyList<string> Columns);

public sealed record ModuleEvent(string DisplayName, string Location, bool Published, IReadOnlyList<string> Consumers);

/// <param name="Limitations">
/// Built from the diagnostics whose file belongs to this module. Generated, never hand-written -
/// a limitation someone has to remember to update is a limitation that goes stale.
/// </param>
public sealed record ModuleDoc(
    string Module,
    IReadOnlyList<ModuleEndpoint> Endpoints,
    IReadOnlyList<ModuleTable> Tables,
    IReadOnlyList<ModuleEvent> Events,
    IReadOnlyList<ModuleEdge> DependsOn,
    IReadOnlyList<ModuleEdge> DependedOnBy,
    IReadOnlyList<string> Limitations);

public static class ModuleDocBuilder
{
    public static IReadOnlyList<ModuleDoc> Build(
        CodeGraph graph,
        ModuleGraph modules,
        IReadOnlyList<string> diagnostics)
    {
        var names = graph.Nodes
            .Select(n => n.Module)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return [.. names.Select(module => Build(graph, modules, diagnostics, module))];
    }

    private static ModuleDoc Build(
        CodeGraph graph,
        ModuleGraph modules,
        IReadOnlyList<string> diagnostics,
        string module)
    {
        var mine = graph.Nodes.Where(n => string.Equals(n.Module, module, StringComparison.Ordinal)).ToList();

        var endpoints = mine
            .Where(n => n.Kind == NodeKind.Endpoint)
            .OrderBy(n => n.DisplayName, StringComparer.Ordinal)
            .Select(n => new ModuleEndpoint(
                n.Id,
                n.DisplayName,
                n.Location,
                graph.ForwardSubgraph(n.Id, new TraversalQuery()).Nodes.Count(x => x.Kind == NodeKind.Table)))
            .ToList();

        // Access across the whole graph: "is this table ever written?" is what a catalogue answers.
        var whole = new Subgraph(graph.Nodes, graph.Edges, new Dictionary<string, int>(StringComparer.Ordinal));
        var dataLayer = AnswerBuilder.DataLayer(whole);

        var tables = dataLayer.Tables
            .Where(t => string.Equals(ModuleOfTable(graph, t.Table), module, StringComparison.Ordinal))
            .Select(t => new ModuleTable(t.Table, t.Access, t.Location, [.. t.Columns.Select(c => c.Name)]))
            .ToList();

        var events = mine
            .Where(n => n.Kind == NodeKind.Event)
            .OrderBy(n => n.DisplayName, StringComparer.Ordinal)
            .Select(n => new ModuleEvent(
                n.DisplayName,
                n.Location,
                graph.Edges.Any(e => e.Kind == EdgeKind.Publishes && string.Equals(e.ToId, n.Id, StringComparison.Ordinal)),
                [
                    .. graph.Edges
                        .Where(e => e.Kind == EdgeKind.Consumes && string.Equals(e.FromId, n.Id, StringComparison.Ordinal))
                        .Select(e => graph.Find(e.ToId)?.DisplayName ?? e.ToId)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal),
                ]))
            .ToList();

        var files = mine.Select(n => n.FilePath).Where(f => !string.IsNullOrEmpty(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var limitations = diagnostics
            .Where(d => files.Any(f => d.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToList();

        return new ModuleDoc(
            module,
            endpoints,
            tables,
            events,
            [.. modules.Edges.Where(e => string.Equals(e.From, module, StringComparison.Ordinal))],
            [.. modules.Edges.Where(e => string.Equals(e.To, module, StringComparison.Ordinal))],
            limitations);
    }

    private static string ModuleOfTable(CodeGraph graph, string table) =>
        graph.Find(NodeId.ForTable(table))?.Module ?? string.Empty;
}
