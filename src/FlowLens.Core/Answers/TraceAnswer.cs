using System.Text.Json.Serialization;

namespace FlowLens.Core.Answers;

/// <summary>
/// The shape of an answer about the graph, as plain data.
/// <para>
/// These records live in Core rather than in the API for the same reason
/// <see cref="Ef.EfModelSnapshot"/> does: they are the answer model, not a transport format.
/// Phase 5's documentation generator and Phase 8's question interface consume exactly these, so
/// the HTTP layer is a thin shell over them rather than the other way round. Nothing here touches
/// ASP.NET; every member is a string, an enum, a number or a list of them.
/// </para>
/// </summary>
/// <param name="Depth">Hops from the start node. Shortest path, because the walk is breadth-first.</param>
public sealed record NodeRef(
    string Id,
    NodeKind Kind,
    RootKind RootKind,
    string DisplayName,
    string Module,
    string FilePath,
    int Line,
    int Depth,
    bool Ambiguous,
    bool Utility)
{
    public string Location => Line > 0 ? $"{FilePath}:{Line}" : FilePath;
}

/// <summary>
/// How strong the claim behind a data edge is. Four buckets, never a number - a score would invent
/// a precision the graph does not have.
/// </summary>
public enum ClaimConfidence
{
    /// <summary>Read straight off the code or EF Core's model.</summary>
    Direct,

    /// <summary>The row was written, so this column was too. Correct, but coarser than an assignment.</summary>
    RowLevel,

    /// <summary>Sound but indirect - a SaveChanges interceptor, linked through the model (L15).</summary>
    Inferred,

    /// <summary>Circumstantial. Construction is not persistence; a bare SaveChanges is not a named write.</summary>
    SecondClass,
}

public sealed record ColumnAnswer(
    string Name,
    string FilePath,
    int Line,
    IReadOnlyList<string> Mechanisms,
    ClaimConfidence Confidence)
{
    public string Location => Line > 0 ? $"{FilePath}:{Line}" : FilePath;
}

/// <param name="Access">"R", "W" or "RW" - derived from the edges, never assumed.</param>
public sealed record TableAnswer(
    string Table,
    string? Schema,
    string Module,
    string FilePath,
    int Line,
    string Access,
    IReadOnlyList<ColumnAnswer> Columns)
{
    public string Location => Line > 0 ? $"{FilePath}:{Line}" : FilePath;
}

public sealed record DataLayerAnswer(IReadOnlyList<TableAnswer> Tables, int ColumnCount);

public sealed record EntryPointGroup(RootKind RootKind, int Count, IReadOnlyList<NodeRef> Nodes);

public sealed record EntryPointsAnswer(int Total, IReadOnlyList<EntryPointGroup> Groups);

/// <param name="PublishedBy">Methods that raise it, with the registry mapping as evidence.</param>
/// <param name="ConsumedBy">IConsumer implementations reached across the async boundary.</param>
public sealed record EventBridgeAnswer(
    string EventId,
    string DisplayName,
    string Module,
    string Location,
    IReadOnlyList<string> PublishedBy,
    IReadOnlyList<string> ConsumedBy);

/// <param name="Code">
/// Stable machine-readable key: raw-sql, unmapped-column, second-class-evidence,
/// ambiguous-implementation, interceptor-columns, truncated. Phase 8 reasons over this, so it is
/// not free text.
/// </param>
/// <param name="Locations">file:line for every site, so a limitation is checkable like any claim.</param>
public sealed record Limitation(string Code, string Message, IReadOnlyList<string> Locations);

/// <param name="Utility">
/// Shared-kernel plumbing hidden from this answer. Reported rather than silently dropped: the tag
/// is a presentation choice, and a consumer must be able to tell "thinned" from "absent".
/// </param>
public sealed record FilteredCounts(int Utility);

/// <param name="Direction">"forward" or "backward".</param>
/// <param name="DataLayer">
/// Forward only. Backward's tables would be the ones on the way IN, which is not a question anyone
/// asks; leaving the field null makes the misreading impossible rather than merely unlikely. The
/// CLI carried the same block under both directions with two different meanings.
/// </param>
/// <param name="EntryPoints">Backward only - the roots that reach the start node, grouped by kind.</param>
/// <param name="MaxDepth">The limit that was APPLIED, not the one requested.</param>
/// <param name="Truncated">True when the walk stopped at the limit with edges still unexplored.</param>
public sealed record TraceAnswer(
    NodeRef Node,
    string Direction,
    int Traversed,
    int MaxDepth,
    bool Truncated,
    IReadOnlyList<NodeRef> Nodes,
    DataLayerAnswer? DataLayer,
    EntryPointsAnswer? EntryPoints,
    IReadOnlyList<EventBridgeAnswer> EventBridges,
    FilteredCounts Filtered,
    IReadOnlyList<Limitation> Limitations);

/// <param name="Route">Split from the display name so a caller never has to parse it.</param>
public sealed record EndpointAnswer(
    string Id,
    string HttpMethod,
    string Route,
    string Module,
    string FilePath,
    int Line)
{
    public string Location => Line > 0 ? $"{FilePath}:{Line}" : FilePath;
}

public sealed record EndpointListAnswer(
    int Total,
    IReadOnlyList<EndpointAnswer> Endpoints,
    IReadOnlyList<Limitation> Limitations);

public sealed record TableListAnswer(
    int Total,
    IReadOnlyList<TableAnswer> Tables,
    IReadOnlyList<Limitation> Limitations);

/// <param name="Status">"ok" or "unavailable".</param>
/// <param name="LoadError">Why the graph could not be read, when Status is "unavailable".</param>
/// <param name="KnownLimitations">
/// The standing caveats that do not vary by question - non-relational stores, owned-navigation
/// reads, column-level reads. Published once here and referenced by code from an answer, rather
/// than copied into every response where they would become noise.
/// </param>
public sealed record GraphStatsAnswer(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LoadError,
    string? Solution,
    int SchemaVersion,
    IReadOnlyDictionary<string, int> NodesByType,
    IReadOnlyDictionary<string, int> EdgesByType,
    IReadOnlyDictionary<string, int> EdgesByMechanism,
    int RootCount,
    int AmbiguousNodes,
    int UtilityNodes,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<Limitation> KnownLimitations,
    string? GraphFileWrittenAt,
    string? LoadedAt,
    int ReloadCount,
    long GraphFileBytes,
    long ApproximateHeapBytes,

    /// <summary>
    /// WHICH file was read. Without it, pointing at the wrong graph.json - a stale copy in another
    /// directory, say - is invisible: every answer would look healthy and be about the wrong repo.
    /// </summary>
    string GraphFilePath,
    IReadOnlyList<string> AttemptedPaths);
