using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowLens.Core.Evals;

/// <summary>
/// Which column set a question is asking about.
/// <para>
/// The two are scored on SEPARATE rows and never summed. <c>AnswerBuilder.ColumnsByTable</c> derives
/// columns from WRITES edges only, so a read column can never be found - recall for that kind is
/// structurally 0. Folding it into one number would drag the write recall down for a reason that has
/// nothing to do with write recall, and would hide how large F9 actually is.
/// </para>
/// </summary>
public enum ColumnKind
{
    Write,
    Read,
}

/// <param name="Access">"R", "W" or "RW" - what the flow does to this table, per the source.</param>
/// <param name="Operation">The SQL statement shape the columns were derived from. Documentation, not asserted.</param>
public sealed record ExpectedTable(
    string Name,
    string Access,
    string Operation,
    IReadOnlyList<string> Columns);

/// <summary>
/// Entry points, grouped exactly as <see cref="Answers.EntryPointsAnswer"/> groups them.
/// A null group is NOT asserted; an empty group asserts that the group must be empty.
/// </summary>
public sealed record ExpectedRoots(
    IReadOnlyList<string>? Endpoint,
    IReadOnlyList<string>? Consumer,
    IReadOnlyList<string>? BackgroundJob);

/// <param name="ConsumedBy">Display names of consumer nodes the flow reaches across a CONSUMES edge.</param>
/// <param name="PublishedBy">Display names of the events the flow publishes.</param>
public sealed record ExpectedEvents(
    IReadOnlyList<string>? ConsumedBy,
    IReadOnlyList<string>? PublishedBy);

/// <summary>
/// The truth about one question, written from ModularCommerce's source.
/// <para>
/// <b>An absent field is not asserted.</b> A backward question carries no <c>Tables</c> and its table
/// set is therefore never scored; an empty list is a different claim entirely - "this must be empty" -
/// and is scored as a precision assertion. Keeping the two apart is why every list here is nullable.
/// </para>
/// </summary>
public sealed record ExpectedAnswer(
    ColumnKind? ColumnKind,
    IReadOnlyList<ExpectedTable>? Tables,
    ExpectedRoots? Roots,
    ExpectedEvents? Events,
    IReadOnlyList<string>? ExternalStores,

    /// <summary>
    /// Limitation codes that MUST appear. A presence assertion, not set equality: the questions do
    /// not enumerate every code an answer may legitimately carry, so extra codes are not penalised.
    /// </summary>
    IReadOnlyList<string>? Limitations,
    IReadOnlyList<string>? Nodes);

/// <param name="Count">How many examples of this failure class the graph holds.</param>
/// <param name="Representative">
/// False is MANDATORY when <paramref name="Count"/> is 1: such a question measures that one example,
/// not the category, and a report that blurs the two invites the reader to generalise from a sample
/// of one.
/// </param>
public sealed record PopulationClaim(
    string Class,
    int Count,
    bool Representative,
    string HowCounted);

/// <param name="ClosedIn">
/// Null while the limitation is open. A phase name means it was CLOSED there - and a closed
/// limitation that fails again is a regression, which is a different finding from an open one
/// behaving as expected.
/// </param>
public sealed record PredictedMiss(string Id, string? ClosedIn);

/// <param name="Evidence">ModularCommerce file:line chain. A question without one is invalid.</param>
public sealed record QuestionNotes(
    string Derivation,
    IReadOnlyList<string> Evidence,
    string Why);

public sealed record QuestionSelector(string Node, string Direction);

public sealed record EvalQuestion(
    string Id,
    string Question,
    QuestionSelector Selector,
    string Category,
    PopulationClaim Population,
    ExpectedAnswer Expected,
    IReadOnlyList<PredictedMiss> ExpectedToFail,
    QuestionNotes Notes)
{
    public TraversalDirection Direction =>
        string.Equals(Selector.Direction, "backward", StringComparison.Ordinal)
            ? TraversalDirection.Backward
            : TraversalDirection.Forward;
}

/// <param name="ColumnRule">The oracle's column rule, verbatim, so the report can quote its own authority.</param>
public sealed record EvalQuestionSet(
    int Version,
    int Phase,
    string DerivedFrom,
    string ColumnRule,
    IReadOnlyList<EvalQuestion> Questions);

/// <summary>
/// One human cross-check of a realised miss.
/// <para>
/// Kept in a SEPARATE file from the questions on purpose. The verdict is produced after a run, and
/// writing it back into questions.json would make "was this expected value adjusted to match the
/// output?" unanswerable from git history. Here, the questions stay frozen and the verdicts have
/// their own diff.
/// </para>
/// </summary>
/// <param name="SourceEvidence">
/// Required when the verdict is <see cref="EvalOracle.Corrected"/>: the ModularCommerce file:line
/// that DISPROVED the expected value. A correction justified by the tool's output rather than by the
/// source is the one failure mode this whole procedure exists to prevent.
/// </param>
public sealed record OracleVerdict(
    string Question,
    string Verdict,
    string SourceEvidence,
    string Note);

public sealed record OracleVerdictSet(IReadOnlyList<OracleVerdict> Verdicts);

public static class EvalOracle
{
    /// <summary>The expected value is in the source; the miss belongs to the tool.</summary>
    public const string Confirmed = "oracle-dogrulandi";

    /// <summary>The expected value was wrong. A finding about the EVAL SET, not about the tool.</summary>
    public const string Corrected = "oracle-duzeltildi";

    /// <summary>The cross-check has not been done yet. Never blank - a blank cell reads as "checked, nothing found".</summary>
    public const string Pending = "beklemede";

    public static bool IsKnown(string verdict) =>
        verdict is Confirmed or Corrected or Pending;
}

/// <summary>
/// Which axis of <see cref="ExpectedAnswer"/> a failure class can show up on.
/// <para>
/// A prediction whose question asserts none of its axes is UNTESTABLE, not wrong: nothing in the
/// answer can move, so the prediction lands in "did not happen" and reads as "the prediction was
/// mistaken about the world". Those are different findings and the first one is a defect in the
/// question, so it is gated rather than reported.
/// </para>
/// <para>
/// Several classes accept more than one axis because the same loss surfaces differently by
/// direction: a raw-SQL table is a missing TABLE going forward and a missing ROOT going backward.
/// One axis of the set is enough.
/// </para>
/// </summary>
public static class FailureAxis
{
    public const string Tables = "tables";
    public const string Roots = "roots";
    public const string Events = "events";
    public const string ExternalStores = "externalStores";
    public const string Limitations = "limitations";
    public const string Nodes = "nodes";

    private static readonly Dictionary<string, string[]> ByClass = new(StringComparer.Ordinal)
    {
        // Non-relational stores are invisible on every other axis: the flow still reaches the
        // class as a Method, so only the ExternalCall typing can move.
        ["F2"] = [ExternalStores],
        ["L17"] = [ExternalStores],

        // Owned-navigation reads cost a table (forward) or a root (backward).
        ["F4"] = [Tables, Roots],
        ["L19"] = [Tables, Roots],

        // Raw SQL costs the table itself, or the root that only reaches it that way.
        ["F6"] = [Tables, Roots],
        ["L6"] = [Tables, Roots],

        // Column-level classes: only a column set can show them.
        ["F5"] = [Tables],
        ["L16"] = [Tables],
        ["L16-4"] = [Tables],
        ["L5"] = [Tables],
        ["L21"] = [Tables],

        // Evidence quality is scored over expected write columns.
        ["F7"] = [Tables],

        // Column reads: a missing read column (forward) or a missing reader root (backward).
        ["F9"] = [Tables, Roots],
        ["L18-2"] = [Tables, Roots],

        // Root kinds.
        ["F8"] = [Roots],
        ["L18-1"] = [Roots],

        // Publish attribution.
        ["L22"] = [Events],
        ["L4"] = [Events],

        // Ambiguity is reported as a limitation code and as the extra implementations themselves.
        ["L3"] = [Limitations, Nodes],
        ["L11"] = [Limitations, Nodes],
    };

    /// <summary>Null when the class is not mapped - an unmapped id is itself a gap worth failing on.</summary>
    public static IReadOnlyList<string>? For(string failureClass) =>
        ByClass.GetValueOrDefault(failureClass);

    /// <summary>Which axes this question actually asserts. An absent field asserts nothing.</summary>
    public static IReadOnlyList<string> Asserted(ExpectedAnswer expected)
    {
        var axes = new List<string>();

        if (expected.Tables is not null)
        {
            axes.Add(Tables);
        }

        if (expected.Roots is not null)
        {
            axes.Add(Roots);
        }

        if (expected.Events is not null)
        {
            axes.Add(Events);
        }

        if (expected.ExternalStores is not null)
        {
            axes.Add(ExternalStores);
        }

        if (expected.Limitations is not null)
        {
            axes.Add(Limitations);
        }

        if (expected.Nodes is not null)
        {
            axes.Add(Nodes);
        }

        return axes;
    }
}

/// <summary>Reads the question set and the optional oracle verdicts. No defaults are invented.</summary>
public static class EvalQuestionFile
{
    public const string DefaultQuestionsPath = "evals/questions.json";
    public const string DefaultVerdictsPath = "evals/oracle-verdicts.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static EvalQuestionSet Read(string path)
    {
        var set = JsonSerializer.Deserialize<EvalQuestionSet>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException($"{path} bos.");

        if (set.Questions.Count == 0)
        {
            throw new InvalidOperationException($"{path} hic soru icermiyor.");
        }

        return set;
    }

    /// <summary>Missing file means no cross-check has been recorded yet, which is not an error.</summary>
    public static IReadOnlyDictionary<string, OracleVerdict> ReadVerdicts(string? path)
    {
        var empty = new Dictionary<string, OracleVerdict>(StringComparer.Ordinal);

        if (path is not { Length: > 0 } || !File.Exists(path))
        {
            return empty;
        }

        var set = JsonSerializer.Deserialize<OracleVerdictSet>(File.ReadAllText(path), Options);

        if (set?.Verdicts is null)
        {
            return empty;
        }

        foreach (var verdict in set.Verdicts)
        {
            empty[verdict.Question] = verdict;
        }

        return empty;
    }
}
