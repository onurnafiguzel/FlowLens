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
public sealed record DiagramEdge(
    string FromId,
    string ToId,
    DiagramEdgeKind Kind,
    string Label,
    bool Ambiguous);

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
