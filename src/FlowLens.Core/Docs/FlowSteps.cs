namespace FlowLens.Core.Docs;

/// <param name="Number">1-based, per node. Not a global counter and not a step in an execution.</param>
/// <param name="Targets">Everything this one call reaches after contraction.</param>
public sealed record FlowStep(int Number, CallSite Site, IReadOnlyList<DiagramEdge> Targets);

/// <param name="Unrecorded">
/// Outgoing edges with no call site. Listed rather than dropped: an interface-to-implementation
/// edge is DI resolution and has no invocation to point at, and a silent absence would read as
/// "there is nothing here".
/// </param>
public sealed record FlowStepGroup(
    DiagramNode From,
    IReadOnlyList<FlowStep> Steps,
    IReadOnlyList<DiagramEdge> Unrecorded);

/// <summary>
/// Groups a node's outgoing edges by the place the call is WRITTEN, and numbers the groups.
/// <para>
/// The unit is the call site, not the edge - and that distinction is the whole point. Measured on
/// all 25 flows: every one of the 18 resolvable sibling groups has at least two siblings that share
/// a single call site, because one <c>carts.GetAsync(...)</c> becomes two boxes once the interface
/// resolves to two implementations, and contraction turns one call into both a repository edge and
/// a table edge. Numbering edges 1..n would have claimed six steps where the source has three, and
/// seventeen where it has eleven. A repeated number is not a defect; it says "one call, several
/// destinations".
/// </para>
/// <para>
/// One source for both the diagram labels and the step list, so the two can never disagree.
/// </para>
/// </summary>
public static class FlowSteps
{
    /// <summary>Nodes with more than one outgoing edge - the only place an order is ambiguous.</summary>
    public static IReadOnlyList<FlowStepGroup> For(FlowDiagram diagram)
    {
        var byNode = diagram.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        return
        [
            .. diagram.Edges
                .GroupBy(e => e.FromId, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Where(g => byNode.ContainsKey(g.Key))
                .Select(g => new FlowStepGroup(
                    byNode[g.Key],
                    Number(g),
                    [.. g.Where(e => e.CallSite is null)]))
                .OrderBy(g => g.From.Id, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Edge to step number, for the diagram labels. Absent for edges with no recorded call site,
    /// and absent entirely for a node with only ONE known call site: a lone "1" on an arrow implies
    /// a "2" the reader will look for and not find. Numbering answers "in what order", and one
    /// position has no order.
    /// </summary>
    public static Dictionary<(string From, string To), int> Numbers(FlowDiagram diagram)
    {
        var map = new Dictionary<(string, string), int>();

        foreach (var group in For(diagram).Where(g => g.Steps.Count > 1))
        {
            foreach (var step in group.Steps)
            {
                foreach (var edge in step.Targets)
                {
                    map[(edge.FromId, edge.ToId)] = step.Number;
                }
            }
        }

        return map;
    }

    private static IReadOnlyList<FlowStep> Number(IEnumerable<DiagramEdge> edges)
    {
        var sites = edges
            .Where(e => e.CallSite is not null)
            .GroupBy(e => (e.CallSite!.FilePath, e.CallSite.Line, e.CallSite.Column))
            .OrderBy(g => g.Key.FilePath, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Line)
            .ThenBy(g => g.Key.Column)
            .ToList();

        return
        [
            .. sites.Select((g, index) => new FlowStep(
                index + 1,
                g.First().CallSite!,
                [.. g.OrderBy(e => e.ToId, StringComparer.Ordinal)])),
        ];
    }
}
