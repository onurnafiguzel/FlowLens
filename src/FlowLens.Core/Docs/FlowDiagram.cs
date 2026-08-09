using FlowLens.Core.Answers;

namespace FlowLens.Core.Docs;

/// <param name="HasDiagnostic">
/// A build diagnostic names this node's file - a raw-SQL site, an unmapped column. Drawn with a
/// warning style and, critically, EXEMPT from pruning: for Discovery the raw-SQL repository is the
/// only node that explains why the diagram shows no tables.
/// </param>
public sealed record DiagramNode(
    string Id,
    NodeKind Kind,
    string DisplayName,
    string Module,
    string Location,
    bool Ambiguous,
    bool HasDiagnostic);

/// <summary>How an edge should read. Style follows meaning, not node kind.</summary>
public enum DiagramEdgeKind
{
    /// <summary>Ordinary synchronous call.</summary>
    Call,

    /// <summary>Reaches a table: READS or WRITES.</summary>
    Data,

    /// <summary>Crosses the async boundary: PUBLISHES or CONSUMES.</summary>
    Async,
}

/// <param name="Label">
/// What was contracted away, when it carries meaning - the entity behind a repository-to-table
/// edge, or the interface behind a call. Empty when contracting several plumbing methods, where a
/// list would be noise.
/// </param>
/// <param name="CallSites">
/// Every place the call that starts this edge is WRITTEN, in source order. Empty when nothing in
/// source writes it - an interface-to-implementation edge is DI resolution, and a fabricated
/// position would be worse than an admitted gap. More than one when the same call is written
/// repeatedly: CheckoutHandler reaches GetByIdempotencyKeyAsync from three separate lines, and
/// showing only the first would quietly answer a narrower question than the reader asked.
/// </param>
public sealed record DiagramEdge(
    string FromId,
    string ToId,
    DiagramEdgeKind Kind,
    string Label,
    bool Ambiguous,
    IReadOnlyList<CallSite>? CallSites = null)
{
    public IReadOnlyList<CallSite> CallSites { get; init; } = CallSites ?? [];

    /// <summary>The first place it is written - the position that orders it among its siblings.</summary>
    public CallSite? CallSite => CallSites.Count == 0 ? null : CallSites[0];
}

/// <param name="Intermediate">Method-kind nodes dropped by the layer filter.</param>
/// <param name="Utility">Shared-kernel plumbing.</param>
/// <param name="Interfaces">Interface declarations whose implementation is on the diagram.</param>
/// <param name="Dataless">Branches reaching no table, event or external call.</param>
public sealed record HiddenCounts(int Intermediate, int Utility, int Interfaces, int Dataless)
{
    public int Total => Intermediate + Utility + Interfaces + Dataless;
}

public sealed record FlowDiagram(
    DiagramNode Start,
    IReadOnlyList<DiagramNode> Nodes,
    IReadOnlyList<DiagramEdge> Edges,
    HiddenCounts Hidden,
    int RawNodeCount,
    IReadOnlyList<Limitation> Limitations,
    DataLayerAnswer DataLayer);
