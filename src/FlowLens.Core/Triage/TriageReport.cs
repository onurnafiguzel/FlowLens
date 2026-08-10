using FlowLens.Core.Answers;

namespace FlowLens.Core.Triage;

/// <summary>
/// What the graph can say about one CONSECUTIVE pair of frames. The stack trace already gives the
/// runtime path; this checks that path against the source, it does not reconstruct it.
/// </summary>
public enum LinkVerdict
{
    /// <summary>An edge exists and one of its call sites is written on the caller's reported line.</summary>
    Verified,

    /// <summary>The edge exists, but no call site sits on that line. The lines the graph does know are reported.</summary>
    LineMismatch,

    /// <summary>No edge, and no path either. The graph does not know this call at all.</summary>
    MissingEdge,

    /// <summary>
    /// No direct edge, but the graph knows a longer path. Something between the two frames is not
    /// in the trace - inlined away, or trimmed. Step 0b measured inlining removing two frames from
    /// a three-frame chain, so this is a regular case, not an exotic one.
    /// </summary>
    SkippedFrames,

    /// <summary>
    /// Both frames are the same method at different lines. Measured in a real capture:
    /// <c>ProductVectorRepository.SearchAsync</c> appears at :65 and :71 because an
    /// <c>await using</c> rethrows during disposal. No edge is expected and none is claimed.
    /// </summary>
    SameMethod,

    /// <summary>At least one side is not a matched node, so there is nothing to check.</summary>
    NotChecked,
}

/// <param name="Through">The interface node bridged over, when one was. Empty otherwise.</param>
/// <param name="KnownLines">Lines the graph records for this edge, when the reported line is not among them.</param>
/// <param name="Path">Node ids of the longer route, when the verdict is SkippedFrames.</param>
public sealed record FrameLink(
    int CallerIndex,
    int CalleeIndex,
    LinkVerdict Verdict,
    string Through,
    IReadOnlyList<int> KnownLines,
    IReadOnlyList<string> Path);

/// <param name="Diagnostic">The build diagnostic text, verbatim.</param>
/// <param name="ExactLine">
/// True when the diagnostic's line equals the error frame's line, not merely its file.
/// Measured across the four captured traces: true for one, false for two - so the report states
/// which it is rather than implying the strong form.
/// </param>
public sealed record DiagnosticHit(string Diagnostic, string FilePath, int Line, bool ExactLine);

/// <param name="Text">Counted, not listed - the exception header and the trailing data block.</param>
public sealed record TraceCounts(int Frames, int Foreign, int Separators, int Text, int Unparsed)
{
    /// <summary>Distinct foreign methods behind <see cref="Foreign"/>. The runtime repeats frames; the two numbers differ.</summary>
    public int DistinctForeign { get; init; }
}

/// <param name="Files">Files whose history was requested: the error frame's, plus every entry point's.</param>
public sealed record CommitSection(GitAnswer Git, IReadOnlyList<string> Files)
{
    public int FileCount => Files.Count;

    public int CommitLines => Git.CommitLines;
}

/// <param name="GraphPath">WHICH graph.json was read. Phase 4 added this because a stale copy in another directory made every answer look healthy.</param>
/// <param name="ErrorPoint">The topmost project frame the graph could match, or null with a reason.</param>
/// <param name="ErrorPointMissing">Why there is no error point. Empty when there is one.</param>
/// <param name="EntryPoints">Straight from AnswerBuilder's backward answer - no second source of truth.</param>
/// <param name="Downstream">Straight from AnswerBuilder's forward answer.</param>
public sealed record TriageReport(
    string ExceptionType,
    string Message,
    TraceCounts Counts,
    string GraphPath,
    int GraphNodes,
    int GraphEdges,
    RepoLocation Repo,
    NodeRef? ErrorPoint,
    string ErrorPointMissing,
    IReadOnlyList<MatchedFrame> Frames,

    /// <summary>Verbatim text of every <c>at</c> line the parser could not read. Empty is the normal case.</summary>
    IReadOnlyList<string> UnparsedLines,
    IReadOnlyList<FrameLink> Links,
    EntryPointsAnswer? EntryPoints,
    DataLayerAnswer? Downstream,
    IReadOnlyList<Limitation> Limitations,
    IReadOnlyList<DiagnosticHit> ErrorPointDiagnostics,
    CommitSection Commits)
{
    /// <summary>True when the graph could not be asked anything - no frame resolved to a node.</summary>
    public bool Unresolved => ErrorPoint is null;

    /// <summary>True when the answer is knowingly partial: git is missing. Maps to the CLI's exit 3.</summary>
    public bool Incomplete => !Commits.Git.Available;
}
