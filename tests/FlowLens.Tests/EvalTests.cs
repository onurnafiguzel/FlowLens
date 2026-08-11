using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FlowLens.Core;
using FlowLens.Core.Answers;
using FlowLens.Core.Evals;

namespace FlowLens.Tests;

/// <summary>
/// The committed graph plus the committed question set.
/// <para>
/// Both are real. A synthetic question set would only ever contain shapes the runner already
/// handles, which is the same mistake Phase 6 recorded: a fixture is a sample, the graph is the
/// population. Neither file is written to by anything in this class.
/// </para>
/// </summary>
public sealed class EvalFixture
{
    public EvalFixture()
    {
        Document = GraphJson.Read(TestPaths.RepositoryGraph);
        Graph = GraphJson.ToGraph(Document);
        Set = EvalQuestionFile.Read(QuestionsPath);
        Verdicts = EvalQuestionFile.ReadVerdicts(VerdictsPath);
    }

    public GraphDocument Document { get; }

    public CodeGraph Graph { get; }

    public EvalQuestionSet Set { get; }

    public IReadOnlyDictionary<string, OracleVerdict> Verdicts { get; }

    public static string QuestionsPath => Path.Combine(RepositoryRoot, "evals", "questions.json");

    public static string VerdictsPath => Path.Combine(RepositoryRoot, "evals", "oracle-verdicts.json");

    public EvalRun Run(CodeGraph? graph = null) =>
        EvalRunner.Run(
            graph ?? Graph,
            Document.Diagnostics,
            TestPaths.RepositoryGraph,
            Set,
            Verdicts);

    public EvalScorecard Score(CodeGraph? graph = null) => EvalScore.Build(Run(graph));

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));

    private static string ThisFile([CallerFilePath] string path = "") => path;
}

/// <summary>
/// The eval set measured as an artefact in its own right.
/// <para>
/// Phase 1's survey said "68 projects" when the answer was 66: the error was in the ORACLE, not in
/// the tool, and had it gone unnoticed it would have been billed to the tool. These tests hold the
/// question set to the same standard the question set holds FlowLens to.
/// </para>
/// </summary>
public sealed class EvalQuestionSetTests : IClassFixture<EvalFixture>
{
    private readonly EvalFixture _fixture;

    public EvalQuestionSetTests(EvalFixture fixture) => _fixture = fixture;

    /// <summary>
    /// An expected value without a source chain cannot be told apart from one copied out of the
    /// tool's output. The chain is the mechanism; "I did not look" is not one.
    /// </summary>
    [Fact]
    public void EveryQuestionCarriesAnEvidenceChain()
    {
        var missing = _fixture.Set.Questions
            .Where(q => q.Notes.Evidence.Count == 0
                || q.Notes.Evidence.Any(e => e.Trim().Length == 0))
            .Select(q => q.Id)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// A class with one example measures that example, not the category. Saying otherwise invites
    /// the reader to generalise from a sample of one - the exact error Phase 6 named.
    /// </summary>
    [Fact]
    public void EveryQuestionDeclaresItsPopulation()
    {
        var faults = new List<string>();

        foreach (var question in _fixture.Set.Questions)
        {
            var population = question.Population;

            if (population.Class.Length == 0 || population.HowCounted.Trim().Length == 0)
            {
                faults.Add($"{question.Id}: sinif ya da howCounted bos");
            }

            if (population.Count < 0)
            {
                faults.Add($"{question.Id}: negatif populasyon");
            }

            if (population.Count == 1 && population.Representative)
            {
                faults.Add($"{question.Id}: count == 1 ama representative true");
            }
        }

        Assert.Empty(faults);
    }

    /// <summary>
    /// A selector that does not resolve is a BROKEN QUESTION, and a broken question that scored
    /// zero would look exactly like a recall loss the tool caused.
    /// </summary>
    [Fact]
    public void EverySelectorResolvesToANode()
    {
        var unresolved = _fixture.Run().Results
            .Where(r => !r.Resolved)
            .Select(r => $"{r.Question.Id}: {r.ResolutionError}")
            .ToList();

        Assert.Empty(unresolved);
    }

    /// <summary>
    /// A predicted failure must have somewhere to happen.
    /// <para>
    /// If a question predicts F2 but never asserts <c>externalStores</c>, nothing in its answer can
    /// move, so the prediction lands in "did not happen" and the report reads it as "the prediction
    /// was mistaken about the world". It was not: it was never measured. The two are different
    /// findings, and only one of them is a fact about FlowLens.
    /// </para>
    /// <para>
    /// Written BEFORE the questions it fails were corrected, deliberately. Added afterwards it
    /// would have been shaped around the one case already known and would have missed the others -
    /// Phase 6's rule that a missing test is chosen from the population, not from the sample.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryPredictedFailureHasAnAxisThatCouldRealiseIt()
    {
        var untestable = new List<string>();

        foreach (var question in _fixture.Set.Questions)
        {
            var asserted = FailureAxis.Asserted(question.Expected);

            foreach (var prediction in question.ExpectedToFail)
            {
                var axes = FailureAxis.For(prediction.Id);

                if (axes is null)
                {
                    untestable.Add($"{question.Id}: {prediction.Id} hicbir eksene eslenmemis");
                    continue;
                }

                if (!axes.Any(axis => asserted.Contains(axis, StringComparer.Ordinal)))
                {
                    untestable.Add(
                        $"{question.Id}: {prediction.Id} icin [{string.Join(", ", axes)}] gerekiyor, "
                        + $"soru [{string.Join(", ", asserted)}] iddia ediyor");
                }
            }
        }

        Assert.Empty(untestable);
    }

    /// <summary>
    /// Running the eval must never touch the question set. If it could, "were the expected values
    /// adjusted to match the output?" would stop being answerable from git history.
    /// </summary>
    [Fact]
    public void RunningTheEvalDoesNotWriteToTheQuestionSet()
    {
        var before = File.ReadAllBytes(EvalFixture.QuestionsPath);

        EvalMarkdown.Render(_fixture.Score());

        Assert.Equal(before, File.ReadAllBytes(EvalFixture.QuestionsPath));
    }
}

public sealed class EvalScoreTests : IClassFixture<EvalFixture>
{
    private readonly EvalFixture _fixture;

    public EvalScoreTests(EvalFixture fixture) => _fixture = fixture;

    /// <summary>
    /// <c>AnswerBuilder.ColumnsByTable</c> derives columns from WRITES edges only, so read-column
    /// recall is structurally 0. Summing the two would drag write recall down for a reason that has
    /// nothing to do with writes, and would hide how large F9 is.
    /// </summary>
    [Fact]
    public void WriteColumnsAndReadColumnsAreScoredOnSeparateRows()
    {
        var card = _fixture.Score();

        var write = card.Metrics.Where(m => m.Level.StartsWith("kolon-yazma", StringComparison.Ordinal)).ToList();
        var read = card.Metrics.Where(m => m.Level.StartsWith("kolon-okuma", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(write);
        Assert.NotEmpty(read);

        // Not merely two rows: no row may carry both kinds.
        Assert.Empty(write.Intersect(read));
    }

    /// <summary>
    /// Phase 3 reported 90% inside EF, 0% outside, and 82% overall - a number that hid both halves.
    /// The split is derived from the graph's own mechanisms, not from a hand-written list.
    /// </summary>
    [Fact]
    public void EfInsideAndEfOutsideAreNeverAveraged()
    {
        var card = _fixture.Score();

        var tableRows = card.Metrics
            .Where(m => m.Level == "tablo")
            .Select(m => m.Scope)
            .ToList();

        Assert.Contains(EfScope.Inside, tableRows);
        Assert.Contains(EfScope.Outside, tableRows);
        Assert.DoesNotContain(EfScope.None, tableRows);

        // The classification must actually separate something, or the split is decorative.
        Assert.NotEmpty(card.Run.EfOutsideTables);
    }

    /// <summary>
    /// Three outcomes, never two. A right answer on second-class evidence is right in recall and
    /// wrong in evidence; one bucket would have to lie about one of them.
    /// </summary>
    [Fact]
    public void EvidenceHasThreeOutcomesNotTwo()
    {
        var outcomes = Enum.GetValues<EvidenceOutcome>();

        Assert.Equal(3, outcomes.Length);

        var card = _fixture.Score();
        var tally = card.Evidence;

        Assert.Equal(
            tally.Total,
            tally.ExpectedMechanism + tally.DifferentButValid + tally.NotFound);

        // Nothing is silently dropped: every expected write column lands in exactly one outcome.
        var expectedWriteColumns = card.Run.Results
            .Where(r => r.Resolved
                && (r.Question.Expected.ColumnKind ?? ColumnKind.Write) == ColumnKind.Write)
            .Sum(r => r.Question.Expected.Tables?.Sum(t => t.Columns.Count) ?? 0);

        Assert.Equal(expectedWriteColumns, tally.Total);
    }

    /// <summary>
    /// Both halves of the 2x2 must be reported, and at least one predicted miss must actually
    /// happen. If none did, either the questions are too easy or the predictions are wrong - and
    /// both are reasons to distrust a clean score, not to celebrate it.
    /// </summary>
    [Fact]
    public void PredictedAndActualMissesAreBothReported()
    {
        var card = _fixture.Score();

        Assert.Equal(6, card.Boxes.Count);

        foreach (var row in (string[])[EvalScore.PredictedOpen, EvalScore.PredictedClosed, EvalScore.NotPredicted])
        {
            Assert.Single(card.Boxes, b => b.Row == row && b.Realized);
            Assert.Single(card.Boxes, b => b.Row == row && !b.Realized);
        }

        // Every resolved question lands in exactly one cell.
        var resolved = card.Run.Results.Count(r => r.Resolved);
        Assert.Equal(resolved, card.Boxes.Sum(b => b.Questions.Count));

        Assert.Contains(_fixture.Set.Questions, q => q.ExpectedToFail.Count > 0);
    }

    /// <summary>
    /// A closed limitation that fails again is a REGRESSION, not "an open limitation behaving as
    /// expected". The two live in different rows because they are different findings, and the
    /// distinction is tested with a constructed prediction rather than waiting for one to appear.
    /// </summary>
    [Fact]
    public void AClosedLimitationThatFailsIsReportedAsARegression()
    {
        var original = _fixture.Set.Questions.First(q => q.ExpectedToFail.Count > 0);

        var reclassified = original with
        {
            ExpectedToFail = [new PredictedMiss(original.ExpectedToFail[0].Id, "Faz 3")],
        };

        var set = _fixture.Set with
        {
            Questions = [.. _fixture.Set.Questions.Select(q => q.Id == original.Id ? reclassified : q)],
        };

        var card = EvalScore.Build(EvalRunner.Run(
            _fixture.Graph, _fixture.Document.Diagnostics, TestPaths.RepositoryGraph, set, _fixture.Verdicts));

        var regressionRow = card.Boxes.Where(b => b.Row == EvalScore.PredictedClosed).ToList();

        Assert.Contains(regressionRow, b => b.Questions.Contains(original.Id));
        Assert.DoesNotContain(
            card.Boxes.Where(b => b.Row == EvalScore.PredictedOpen),
            b => b.Questions.Contains(original.Id));
    }

    /// <summary>
    /// F1..F10 and L1..L22, every row either naming a question or carrying the reason it cannot.
    /// A silent gap here is the shape of the four silent errors Phase 3 shipped.
    /// </summary>
    [Fact]
    public void EveryFailureClassInTheMetaTableHasAQuestionOrAReason()
    {
        var card = _fixture.Score();

        var expectedIds = Enumerable.Range(1, 10).Select(i => $"F{i}")
            .Concat(Enumerable.Range(1, 22).Select(i => $"L{i}"))
            .ToList();

        Assert.Equal(expectedIds, card.Meta.Select(m => m.Id).ToList());

        var silent = card.Meta
            .Where(m => m.Questions.Count == 0 && m.Reason.Trim().Length == 0)
            .Select(m => m.Id)
            .ToList();

        Assert.Empty(silent);
    }

    /// <summary>
    /// Every realised miss carries an oracle verdict, and it is never blank. "beklemede" is a
    /// verdict - a blank cell would read as "checked, nothing found".
    /// </summary>
    [Fact]
    public void EveryActualMissCarriesAnOracleVerdict()
    {
        var blank = _fixture.Run().Results
            .Where(r => r.Resolved && r.MissRealized)
            .Where(r => !EvalOracle.IsKnown(r.OracleVerdict))
            .Select(r => $"{r.Question.Id}: \"{r.OracleVerdict}\"")
            .ToList();

        Assert.Empty(blank);
    }

    /// <summary>
    /// A correction justified by the tool's OUTPUT rather than by the source is the one failure
    /// mode the whole procedure exists to prevent, so a corrected verdict must cite a file:line.
    /// </summary>
    [Fact]
    public void EveryCorrectedOracleCitesASourceLine()
    {
        var uncited = _fixture.Run().Results
            .Where(r => r.OracleVerdict == EvalOracle.Corrected)
            .Where(r => !System.Text.RegularExpressions.Regex.IsMatch(r.OracleEvidence, @"\.cs:\d+"))
            .Select(r => $"{r.Question.Id}: \"{r.OracleEvidence}\"")
            .ToList();

        Assert.Empty(uncited);
    }

    /// <summary>
    /// F10, held at the type level rather than by convention: a backward answer has no data layer,
    /// so "the tables on the way in" can never be misread as "the tables this reaches".
    /// </summary>
    [Fact]
    public void ABackwardAnswerCarriesNoDataLayer()
    {
        var query = new TraversalQuery();

        foreach (var question in _fixture.Set.Questions.Where(q => q.Direction == TraversalDirection.Backward))
        {
            var id = NodeResolver.Resolve(_fixture.Graph, question.Selector.Node)!;
            var subgraph = _fixture.Graph.BackwardSubgraph(id, query);

            var answer = AnswerBuilder.Build(
                _fixture.Graph, subgraph, id, TraversalDirection.Backward, query, _fixture.Document.Diagnostics);

            Assert.Null(answer.DataLayer);
            Assert.NotNull(answer.EntryPoints);
        }
    }
}

public sealed class EvalReportTests : IClassFixture<EvalFixture>
{
    private readonly EvalFixture _fixture;

    public EvalReportTests(EvalFixture fixture) => _fixture = fixture;

    [Fact]
    public void TheReportIsByteIdenticalForTheSameGraph()
    {
        Assert.Equal(
            EvalMarkdown.Render(_fixture.Score()),
            EvalMarkdown.Render(_fixture.Score()));
    }

    /// <summary>
    /// Phase 4's permutation form, and it is stronger than "two runs match": it holds for every
    /// ordering of the input, so a result that depended on discovery order could not hide behind a
    /// stable output order.
    /// </summary>
    [Fact]
    public void ReversingTheGraphChangesNothing()
    {
        var reversed = new CodeGraph(
            [.. _fixture.Graph.Nodes.Reverse()],
            [.. _fixture.Graph.Edges.Reverse()]);

        Assert.Equal(
            EvalMarkdown.Render(_fixture.Score()),
            EvalMarkdown.Render(_fixture.Score(reversed)));
    }

    /// <summary>Phase 5's rule: an artefact that records its own generation time can never be byte-identical twice.</summary>
    [Fact]
    public void TheReportRecordsNoTimestamp()
    {
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}", EvalMarkdown.Render(_fixture.Score()));
    }

    /// <summary>
    /// The markdown gate, carried forward from Phase 5 and Phase 6. A block-starting line without a
    /// blank line before it is swallowed as lazy continuation: nothing errors, the file simply
    /// renders wrong. Measured once at 10 of 10 module pages, and again on the first run of the
    /// triage report.
    /// </summary>
    [Fact]
    public void NoBlockStartsWithoutABlankLineBeforeIt()
    {
        var lines = EvalMarkdown.Render(_fixture.Score()).Split('\n');
        var violations = new List<string>();

        for (var i = 1; i < lines.Length; i++)
        {
            if (!StartsABlock(lines[i]) || lines[i - 1].Trim().Length == 0 || Continues(lines[i], lines[i - 1]))
            {
                continue;
            }

            violations.Add($"{i + 1}: \"{lines[i]}\" after \"{lines[i - 1]}\"");
        }

        Assert.Empty(violations);

        static bool StartsABlock(string line) =>
            line.StartsWith("**", StringComparison.Ordinal)
            || line.StartsWith('#')
            || line.StartsWith("- ", StringComparison.Ordinal)
            || line.StartsWith('|')
            || line.StartsWith('>');

        static bool Continues(string line, string previous) =>
            (line.StartsWith('|') && previous.StartsWith('|'))
            || (line.StartsWith('>') && previous.StartsWith('>'))
            || (line.StartsWith("- ", StringComparison.Ordinal)
                && (previous.StartsWith("- ", StringComparison.Ordinal) || previous.StartsWith("  ", StringComparison.Ordinal)));
    }
}

/// <summary>
/// "Measuring the narrow waist measures all five consumers" is a claim, and a claim in this project
/// is a gate rather than a justification. The HTTP surface must return the same table and column
/// sets the eval scored.
/// </summary>
public sealed class EvalSurfaceParityTests : IClassFixture<EvalFixture>
{
    private readonly EvalFixture _fixture;

    public EvalSurfaceParityTests(EvalFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SurfacesAgree()
    {
        using var host = new ApiHost(TestPaths.RepositoryGraph);
        using var client = host.CreateClient();

        var query = new TraversalQuery();
        var sampled = 0;

        foreach (var question in _fixture.Set.Questions
            .Where(q => q.Direction == TraversalDirection.Forward && q.Expected.Tables is not null)
            .OrderBy(q => q.Id, StringComparer.Ordinal))
        {
            var id = NodeResolver.Resolve(_fixture.Graph, question.Selector.Node)!;
            var subgraph = _fixture.Graph.ForwardSubgraph(id, query);

            var direct = AnswerBuilder.Build(
                _fixture.Graph, subgraph, id, TraversalDirection.Forward, query, _fixture.Document.Diagnostics);

            var body = await client.GetFromJsonAsync<JsonElement>(
                $"/trace?node={Uri.EscapeDataString(id)}");

            Assert.Equal(Shape(direct), Shape(body));
            sampled++;
        }

        Assert.NotEqual(0, sampled);
    }

    private static IReadOnlyList<string> Shape(TraceAnswer answer) =>
    [
        .. (answer.DataLayer?.Tables ?? [])
            .SelectMany(t => t.Columns.Count == 0
                ? (IEnumerable<string>)[$"{t.Table}|{t.Access}"]
                : t.Columns.Select(c => $"{t.Table}|{t.Access}|{c.Name}"))
            .Order(StringComparer.Ordinal),
    ];

    private static IReadOnlyList<string> Shape(JsonElement body)
    {
        var shaped = new List<string>();

        if (!body.TryGetProperty("dataLayer", out var layer) || layer.ValueKind != JsonValueKind.Object)
        {
            return shaped;
        }

        foreach (var table in layer.GetProperty("tables").EnumerateArray())
        {
            var name = table.GetProperty("table").GetString();
            var access = table.GetProperty("access").GetString();
            var columns = table.GetProperty("columns").EnumerateArray().ToList();

            if (columns.Count == 0)
            {
                shaped.Add($"{name}|{access}");
                continue;
            }

            shaped.AddRange(columns.Select(c => $"{name}|{access}|{c.GetProperty("name").GetString()}"));
        }

        shaped.Sort(StringComparer.Ordinal);
        return shaped;
    }
}
