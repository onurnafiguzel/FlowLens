using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FlowLens.Core;
using FlowLens.Core.Answers;
using FlowLens.Core.Triage;

namespace FlowLens.Tests;

/// <summary>
/// The real graph plus the four captured stack traces.
/// <para>
/// The traces are REAL, not written by hand: step 0c ran ModularCommerce's own compiled assemblies
/// against real Postgres containers and captured what actually came out. A hand-written trace would
/// only ever match the parser's own assumptions, which is precisely what a parser test must not do.
/// Each file records which rung of the ladder it came from in its header.
/// </para>
/// </summary>
public sealed class TriageFixture
{
    public TriageFixture()
    {
        Document = GraphJson.Read(TestPaths.RepositoryGraph);
        Graph = GraphJson.ToGraph(Document);

        Snapshot = new GraphSnapshot(
            Document,
            Graph,
            Document.Diagnostics,
            DateTime.UnixEpoch,
            0,
            DateTime.UnixEpoch,
            0);
    }

    public GraphDocument Document { get; }

    public CodeGraph Graph { get; }

    public GraphSnapshot Snapshot { get; }

    public static string Directory =>
        Path.Combine(Path.GetDirectoryName(ThisFile())!, "Fixtures", "StackTraces");

    /// <summary>Every captured trace, by its short name. Ordered so failures name files predictably.</summary>
    public static IReadOnlyList<string> Names => ["A", "A2", "B", "C", "D"];

    public static string Read(string name)
    {
        var matches = System.IO.Directory.GetFiles(Directory, $"{name}-*.txt");

        if (matches.Length != 1)
        {
            throw new FileNotFoundException(
                $"Expected exactly one captured trace named {name}-*.txt in {Directory}, found {matches.Length}. " +
                "These fixtures are real captures; regenerating them needs Docker and the target's built assemblies.");
        }

        return File.ReadAllText(matches[0]);
    }

    public TriageReport Build(string name, string? repo = null) =>
        TriageBuilder.Build(Snapshot, TestPaths.RepositoryGraph, Read(name), repo);

    private static string ThisFile([CallerFilePath] string path = "") => path;
}

/// <summary>
/// The parser is pinned against the shapes step 0a MEASURED, not the ones it was assumed to emit.
/// Two of these exist only because the measurement contradicted the assumption.
/// </summary>
public sealed class StackTraceParserTests : IClassFixture<TriageFixture>
{
    private readonly TriageFixture _fixture;

    public StackTraceParserTests(TriageFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Nothing is dropped. Every input line lands in exactly one class, and the classes add up to
    /// the input - roadmap rule 8 applied to the parser itself. A parser that silently skips what
    /// it cannot read is indistinguishable from one that read everything.
    /// </summary>
    [Theory]
    [InlineData("A")]
    [InlineData("A2")]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    public void EveryInputLineIsClassified(string name)
    {
        var text = TriageFixture.Read(name);
        var parsed = StackTraceParser.Parse(text);

        var expected = text.ReplaceLineEndings("\n").Split('\n').Length;

        Assert.Equal(expected, parsed.Lines.Count);

        Assert.Equal(
            parsed.Lines.Count,
            parsed.Count(LineKind.Frame)
            + parsed.Count(LineKind.Unparsed)
            + parsed.Count(LineKind.Separator)
            + parsed.Count(LineKind.Text));
    }

    /// <summary>
    /// Every "at" line becomes a frame or is reported verbatim. The relation is asserted, not a
    /// count: a hardcoded number would be a measurement pretending to be a test (roadmap rule 7).
    /// </summary>
    [Theory]
    [InlineData("A")]
    [InlineData("A2")]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    public void EveryAtLineBecomesAFrameOrIsReported(string name)
    {
        var text = TriageFixture.Read(name);
        var parsed = StackTraceParser.Parse(text);

        var atLines = text
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Count(line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));

        Assert.Equal(atLines, parsed.Frames.Count + parsed.Unparsed.Count);
        Assert.Empty(parsed.Unparsed);
    }

    /// <summary>
    /// Measured in 0a: async METHODS come out demangled, so a frame's key is the name a developer
    /// would search for. If a runtime update reverted that, every project frame would turn into
    /// MoveNext and match nothing - silently.
    /// </summary>
    [Fact]
    public void AnAsyncMethodFrameCarriesItsOwnNameNotMoveNext()
    {
        var parsed = StackTraceParser.Parse(TriageFixture.Read("A"));

        var frame = Assert.Single(
            parsed.Frames,
            f => f.Key.EndsWith("NaiveReservationStrategy.ReserveAsync", StringComparison.Ordinal) && f.Line == 37);

        Assert.Equal("ReserveAsync", frame.Method);
        Assert.DoesNotContain("MoveNext", frame.Key, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of 0a: async LAMBDAS and local functions are NOT demangled by the runtime, so
    /// the MoveNext and closure handling is still load-bearing. Measured on a real capture.
    /// </summary>
    [Fact]
    public void ACompilerGeneratedFrameResolvesToItsDeclaringMethod()
    {
        var parsed = StackTraceParser.Parse(TriageFixture.Read("C"));

        Assert.DoesNotContain(parsed.Frames, f => f.Key.Contains("<", StringComparison.Ordinal));
        Assert.DoesNotContain(parsed.Frames, f => f.Method == "MoveNext");
    }

    /// <summary>
    /// Also 0a: parameter types render as CLR short names. Recorded here because the matcher's
    /// alias table exists only for this, and a change would otherwise be invisible.
    /// </summary>
    [Fact]
    public void ParameterTypesAreClrShortNames()
    {
        var parsed = StackTraceParser.Parse(TriageFixture.Read("B"));

        var frame = parsed.Frames.First(f => f.Key.EndsWith("ProductVectorRepository.SearchAsync", StringComparison.Ordinal));

        Assert.Equal(["Single[]", "Int32", "CancellationToken"], frame.ParameterTypes);
    }

    /// <summary>
    /// A header is only taken from a line that LOOKS like one. An incident is usually pasted with a
    /// log prefix or a comment above it, and reporting that line as the exception type would be a
    /// confidently wrong field rather than a visible failure.
    /// </summary>
    [Fact]
    public void ALineThatIsNotAnExceptionHeaderIsNotReadAsOne()
    {
        var parsed = StackTraceParser.Parse(
            "# a comment about where this came from\n"
            + "2026-08-10 11:02:03 ERR request failed\n"
            + "System.InvalidOperationException: something broke\n"
            + "   at Some.Type.Method(Int32 x) in C:\\repo\\src\\File.cs:line 9\n");

        Assert.Equal("System.InvalidOperationException", parsed.ExceptionType);
        Assert.Equal("something broke", parsed.Message);
    }
}

/// <summary>
/// Triage is Phase 4's backward traversal with a different input. These tests hold that line: the
/// report may never say something AnswerBuilder would not, and it may never be quiet about what it
/// could not see.
/// </summary>
public sealed class TriageReportTests : IClassFixture<TriageFixture>
{
    private readonly TriageFixture _fixture;

    public TriageReportTests(TriageFixture fixture) => _fixture = fixture;

    /// <summary>
    /// THE THIRD VERDICT. A project frame the graph has no node for is NAMED, not dropped.
    /// Measured: 147 of the target's 300 source files have no node at all, so this is the regular
    /// case. Money.Add is a real one - it throws, and only Money.Create is in the graph.
    /// </summary>
    [Fact]
    public void AFrameTheGraphCannotSeeIsNamedNotDropped()
    {
        var report = _fixture.Build("C");

        var unseen = report.Frames.Where(f => f.Verdict == FrameVerdict.NotInGraph).ToList();

        Assert.NotEmpty(unseen);
        Assert.Contains(unseen, f => f.Frame.Key.EndsWith("Money.Add", StringComparison.Ordinal));

        var markdown = TriageMarkdown.Render(report);

        foreach (var frame in unseen)
        {
            Assert.Contains(frame.Frame.Key, markdown, StringComparison.Ordinal);
        }

        // And the report must not pretend it answered: no error point, and it says why.
        Assert.Null(report.ErrorPoint);
        Assert.NotEmpty(report.ErrorPointMissing);

        // Nor may it announce exit 3 when the run will exit 4.
        Assert.DoesNotContain("Çıkış kodu **3**", markdown, StringComparison.Ordinal);
        Assert.Contains("çıkış kodu **4**", markdown, StringComparison.Ordinal);
    }

    /// <summary>Every frame reaches the page, whatever its verdict. The verdict may vary; the presence may not.</summary>
    [Theory]
    [InlineData("A")]
    [InlineData("A2")]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    public void EveryFrameInTheInputAppearsInTheReport(string name)
    {
        var report = _fixture.Build(name);
        var parsed = StackTraceParser.Parse(TriageFixture.Read(name));

        Assert.Equal(parsed.Frames.Count, report.Frames.Count);

        var markdown = TriageMarkdown.Render(report);
        var missing = report.Frames
            .Where(f => !markdown.Contains(f.Frame.Key, StringComparison.Ordinal))
            .Select(f => f.Frame.Key)
            .ToList();

        Assert.True(missing.Count == 0, $"{missing.Count} cerceve raporda yok: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// NO THIRD SOURCE OF TRUTH. The report's entry points must be exactly what a /backward call
    /// would return for the same node. If these ever diverge, one of them is lying and the reader
    /// cannot tell which.
    /// </summary>
    [Theory]
    [InlineData("A")]
    [InlineData("A2")]
    [InlineData("B")]
    [InlineData("D")]
    public void EntryPointsMatchWhatBackwardAlreadyAnswers(string name)
    {
        var report = _fixture.Build(name);
        var node = report.ErrorPoint!;

        var query = new TraversalQuery(new TriageQuery().MaxDepth, new TriageQuery().IncludeUtility);

        var backward = AnswerBuilder.Build(
            _fixture.Graph,
            _fixture.Graph.BackwardSubgraph(node.Id, query),
            node.Id,
            TraversalDirection.Backward,
            query,
            _fixture.Document.Diagnostics);

        Assert.Equal(
            backward.EntryPoints!.Groups.Select(g => (g.RootKind, g.Count)),
            report.EntryPoints!.Groups.Select(g => (g.RootKind, g.Count)));

        Assert.Equal(
            backward.EntryPoints.Groups.SelectMany(g => g.Nodes.Select(n => n.Id)),
            report.EntryPoints.Groups.SelectMany(g => g.Nodes.Select(n => n.Id)));
    }

    /// <summary>Roots are grouped by role, never flattened into one number ("2 endpoint", not "2 root").</summary>
    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void EntryPointsAreGroupedByRootKind(string name)
    {
        var report = _fixture.Build(name);

        Assert.NotNull(report.EntryPoints);
        Assert.NotEmpty(report.EntryPoints!.Groups);
        Assert.All(report.EntryPoints.Groups, g => Assert.NotEqual(RootKind.None, g.RootKind));

        Assert.Equal(
            report.EntryPoints.Total,
            report.EntryPoints.Groups.Sum(g => g.Count));
    }

    /// <summary>
    /// The strong claim, and only when it is true. Fixture A throws on the very line a raw-SQL
    /// diagnostic points at; A2 and B throw in the same FILE as a diagnostic but not on its line.
    /// The report must distinguish those, because "this flow uses raw SQL somewhere" and "the
    /// exception was thrown inside the raw SQL" are different facts.
    /// </summary>
    [Fact]
    public void AnExactLineHitIsClaimedOnlyWhenTheLinesReallyMatch()
    {
        var exact = _fixture.Build("A");
        var hit = Assert.Single(exact.ErrorPointDiagnostics, d => d.ExactLine);

        Assert.Equal(exact.Frames.First(f => f.Verdict == FrameVerdict.Matched).Frame.Line, hit.Line);
        Assert.Contains("raw SQL", hit.Diagnostic, StringComparison.Ordinal);

        foreach (var name in new[] { "A2", "B" })
        {
            var report = _fixture.Build(name);

            Assert.NotEmpty(report.ErrorPointDiagnostics);
            Assert.DoesNotContain(report.ErrorPointDiagnostics, d => d.ExactLine);
        }
    }

    /// <summary>
    /// SOUNDNESS, the Phase 5 shape. Every pair the report calls "verified" must correspond to a
    /// real edge - direct, or across exactly ONE call-site-less hop, which is what an
    /// interface-to-implementation edge looks like. A checker that blесses a route it cannot
    /// justify is worse than one that says nothing.
    /// </summary>
    [Theory]
    [InlineData("A")]
    [InlineData("A2")]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    public void AVerifiedFrameLinkReallyExistsInTheGraph(string name)
    {
        var report = _fixture.Build(name);
        var unjustified = new List<string>();

        foreach (var link in report.Links.Where(l => l.Verdict == LinkVerdict.Verified))
        {
            var caller = report.Frames[link.CallerIndex];
            var callee = report.Frames[link.CalleeIndex];

            var from = caller.Node!.Id;
            var to = callee.Node!.Id;

            var direct = _fixture.Graph.Edges.Any(e =>
                e.Kind == EdgeKind.Calls
                && e.FromId == from
                && e.ToId == to
                && e.CallSites.Any(s => s.Line == caller.Frame.Line));

            var bridged = _fixture.Graph.Edges.Any(first =>
                first.Kind == EdgeKind.Calls
                && first.FromId == from
                && first.CallSites.Any(s => s.Line == caller.Frame.Line)
                && _fixture.Graph.Edges.Any(second =>
                    second.Kind == EdgeKind.Calls
                    && second.CallSites.Count == 0
                    && second.FromId == first.ToId
                    && second.ToId == to));

            if (!direct && !bridged)
            {
                unjustified.Add($"{from} -> {to} @{caller.Frame.Line}");
            }
        }

        Assert.True(
            unjustified.Count == 0,
            $"{unjustified.Count} bag gerekcesiz 'dogrulandi' aldi: {string.Join(" | ", unjustified)}");
    }

    /// <summary>
    /// Step 0b measured inlining removing two frames from a three-frame chain, and 38% of this
    /// target's method nodes are synchronous, so a gap between adjacent frames is a regular case.
    /// When the graph knows a longer route, saying only "not in the graph" throws that away.
    /// </summary>
    [Theory]
    [InlineData("A")]
    [InlineData("A2")]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    public void AGraphKnownLongerPathIsNotReportedAsUnknown(string name)
    {
        var report = _fixture.Build(name);
        var wrong = new List<string>();

        foreach (var link in report.Links.Where(l => l.Verdict == LinkVerdict.MissingEdge))
        {
            var from = report.Frames[link.CallerIndex].Node!.Id;
            var to = report.Frames[link.CalleeIndex].Node!.Id;

            if (Reachable(from, to, 4))
            {
                wrong.Add($"{from} -> {to}");
            }
        }

        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} bag 'graph'ta yok' dedi ama graph bir yol biliyor: {string.Join(" | ", wrong)}");

        Assert.All(
            report.Links.Where(l => l.Verdict == LinkVerdict.SkippedFrames),
            link => Assert.NotEmpty(link.Path));
    }

    /// <summary>
    /// THE BRIDGE. A stack trace has no interface frame - DI dispatches straight to the
    /// implementation - while the graph has an interface node between the two. Fixture D is the
    /// only capture that reaches the failure the way production does, through the contract adapter,
    /// so it is the only one that exercises this at all.
    /// </summary>
    [Fact]
    public void AnInterfaceHopIsBridgedAndSaidSo()
    {
        var report = _fixture.Build("D");

        var bridged = report.Links
            .Where(l => l.Verdict == LinkVerdict.Verified && l.Through.Length > 0)
            .ToList();

        Assert.NotEmpty(bridged);

        foreach (var link in bridged)
        {
            // The bridged node must really sit between them, and the second edge must be the
            // call-site-less kind - that is what an interface-to-implementation edge looks like.
            var caller = report.Frames[link.CallerIndex].Node!.Id;
            var callee = report.Frames[link.CalleeIndex].Node!.Id;

            Assert.Contains(_fixture.Graph.Edges, e =>
                e.Kind == EdgeKind.Calls && e.FromId == caller && e.ToId == link.Through);

            Assert.Contains(_fixture.Graph.Edges, e =>
                e.Kind == EdgeKind.Calls && e.FromId == link.Through && e.ToId == callee && e.CallSites.Count == 0);
        }

        Assert.Contains("arayüz köprüsü", TriageMarkdown.Render(report), StringComparison.Ordinal);
    }

    /// <summary>
    /// Step 0b measured the JIT deleting two frames from a three-frame chain, so a real trace can
    /// arrive with a gap. This models exactly that: fixture D minus its middle project frame. The
    /// report must not answer "the graph does not know this call" when the graph knows a route -
    /// the reader would take that as evidence the call is not there.
    /// </summary>
    [Fact]
    public void AGapWhereTheGraphKnowsARouteIsReportedAsSkippedFrames()
    {
        // Two frames the graph connects only through a FOUR-hop route: CheckoutHandler reaches
        // NaiveReservationStrategy via IStockReservationService, StockReservationService and
        // IReservationStrategy. Both ends and the caller's line come from the graph itself, so the
        // input models a dropped middle rather than asserting a shape someone typed.
        var callee = Node("NaiveReservationStrategy.ReserveAsync");
        var caller = Node("CheckoutHandler.HandleAsync");

        var site = _fixture.Graph.Edges
            .Single(e => e.Kind == EdgeKind.Calls
                && e.FromId == caller.Id
                && e.ToId.EndsWith("IStockReservationService.ReserveAsync(System.Guid, int, System.Threading.CancellationToken)", StringComparison.Ordinal))
            .CallSites[0];

        var text =
            "System.InvalidOperationException: middle frames absent\n"
            + $"   at {callee.Id} in C:\\repo\\{callee.FilePath.Replace('/', '\\')}:line {callee.Line}\n"
            + $"   at {caller.Id} in C:\\repo\\{caller.FilePath.Replace('/', '\\')}:line {site.Line}\n";

        var report = TriageBuilder.Build(_fixture.Snapshot, TestPaths.RepositoryGraph, text, null);

        var gap = Assert.Single(report.Links);

        Assert.Equal(LinkVerdict.SkippedFrames, gap.Verdict);
        Assert.True(gap.Path.Count >= 2, $"Yol en az 2 hop olmali, {gap.Path.Count} geldi.");
        Assert.Contains("atlanmış çerçeve olabilir", TriageMarkdown.Render(report), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BRIDGE MAY NOT WIDEN. Two frames whose only route runs through a middle node reached by
    /// a REAL call - not by DI - must never be called verified: something ran in between, and the
    /// caller's line points at the call to the middle node, not to the callee.
    /// <para>
    /// This case is chosen from the graph rather than from a fixture on purpose. Widening the
    /// bridge to two hops passed the whole suite when the fixtures were the only witnesses, because
    /// none of them happens to contain such a pair - the same shape as Phase 5's first mutation,
    /// where the test was right and the population was silent. Measured: 310 such pairs exist.
    /// </para>
    /// </summary>
    [Fact]
    public void ATwoHopRouteThroughARealCallIsNotVerified()
    {
        var callSites = _fixture.Graph.Edges
            .Where(e => e.Kind == EdgeKind.Calls && e.CallSites.Count > 0)
            .ToList();

        var direct = callSites
            .Concat(_fixture.Graph.Edges.Where(e => e.Kind == EdgeKind.Calls))
            .Select(e => (e.FromId, e.ToId))
            .ToHashSet();

        var pair = callSites
            .SelectMany(first => callSites
                .Where(second => second.FromId == first.ToId)
                .Select(second => (First: first, Second: second)))
            .Where(p => p.Second.ToId != p.First.FromId
                && !direct.Contains((p.First.FromId, p.Second.ToId)))
            .OrderBy(p => p.First.FromId, StringComparer.Ordinal)
            .ThenBy(p => p.Second.ToId, StringComparer.Ordinal)
            .First();

        var caller = _fixture.Graph.Find(pair.First.FromId)!;
        var callee = _fixture.Graph.Find(pair.Second.ToId)!;

        var text =
            "System.InvalidOperationException: two real hops apart\n"
            + $"   at {callee.Id} in C:\\repo\\{callee.FilePath.Replace('/', '\\')}:line {callee.Line}\n"
            + $"   at {caller.Id} in C:\\repo\\{caller.FilePath.Replace('/', '\\')}:line {pair.First.CallSites[0].Line}\n";

        var report = TriageBuilder.Build(_fixture.Snapshot, TestPaths.RepositoryGraph, text, null);
        var link = Assert.Single(report.Links);

        Assert.True(
            link.Verdict != LinkVerdict.Verified,
            $"{caller.DisplayName} -> {callee.DisplayName}: aradan gecen {_fixture.Graph.Find(pair.First.ToId)!.DisplayName} " +
            "GERCEK bir cagri ile ulasiliyor, DI ile degil. Bu 'dogrulandi' olamaz.");
    }

    private Node Node(string suffix) =>
        _fixture.Graph.Nodes.Single(n => n.Id.Contains(suffix + "(", StringComparison.Ordinal));

    /// <summary>
    /// Measured in a real capture: ProductVectorRepository.SearchAsync appears at :65 and :71
    /// because an await using rethrows during disposal. Two frames, one method, and no edge from a
    /// method to itself - the report must say so rather than reporting a missing edge, which would
    /// read as a gap in the graph.
    /// </summary>
    [Fact]
    public void TheSameMethodTwiceIsNotReportedAsAMissingEdge()
    {
        var report = _fixture.Build("B");

        var repeated = report.Frames
            .Where(f => f.Verdict == FrameVerdict.Matched)
            .GroupBy(f => f.Node!.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.NotEmpty(repeated);

        var lines = repeated[0].Select(f => f.Frame.Line).Distinct().ToList();
        Assert.True(lines.Count > 1, "Ayni metot ayni satirda iki kez gorunuyor; vaka bu degil.");

        var self = report.Links.Where(l =>
            report.Frames[l.CallerIndex].Node?.Id == report.Frames[l.CalleeIndex].Node?.Id
            && report.Frames[l.CallerIndex].Node is not null);

        Assert.All(self, link => Assert.Equal(LinkVerdict.SameMethod, link.Verdict));
        Assert.Contains("aynı metot", TriageMarkdown.Render(report), StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime repeats frames - NpgsqlDataReader.NextResult twice in a row, RelationalCommand
    /// three times - so "how many foreign frames" and "how many foreign methods" are two different
    /// numbers. Reporting only one of them would make a 14-line trace look either padded or thin.
    /// </summary>
    [Fact]
    public void RepeatedForeignFramesAreCountedApartFromDistinctMethods()
    {
        var report = _fixture.Build("A");
        var counts = report.Counts;

        Assert.True(
            counts.Foreign > counts.DistinctForeign,
            $"Bu yakalamada tekrarlanan framework cercevesi bekleniyordu: {counts.Foreign} cerceve, " +
            $"{counts.DistinctForeign} farkli metot.");

        var foreign = report.Frames.Where(f => f.Verdict == FrameVerdict.Foreign).ToList();

        Assert.Equal(foreign.Count, counts.Foreign);
        Assert.Equal(foreign.Select(f => f.Frame.Key).Distinct(StringComparer.Ordinal).Count(), counts.DistinctForeign);

        var markdown = TriageMarkdown.Render(report);
        Assert.Contains($"{counts.Foreign} ({counts.DistinctForeign} farklı metot)", markdown, StringComparison.Ordinal);
    }

    /// <summary>Which graph and which repository. Phase 4 added graphFilePath because a stale copy elsewhere made every answer look healthy.</summary>
    [Fact]
    public void TheReportNamesItsGraphAndItsRepository()
    {
        var report = _fixture.Build("A");
        var markdown = TriageMarkdown.Render(report);

        Assert.Contains(report.GraphPath, markdown, StringComparison.Ordinal);
        Assert.Equal(RepoOrigin.DerivedFromStackTrace, report.Repo.Origin);
        Assert.Contains(report.Repo.Root, markdown, StringComparison.Ordinal);
        Assert.Contains("yığın izinden türetildi", markdown, StringComparison.Ordinal);
    }

    /// <summary>An explicitly named repository is never quietly replaced - GraphPathResolver's rule.</summary>
    [Fact]
    public void AGivenRepoIsNeverReplacedBySomethingElse()
    {
        var missing = Path.Combine(Path.GetTempPath(), "flowlens-triage-no-such-repo");
        var report = _fixture.Build("A", missing);

        Assert.Equal(RepoOrigin.NotFound, report.Repo.Origin);
        Assert.Contains(missing, report.Repo.Attempts.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.False(report.Commits.Git.Available);
    }

    /// <summary>
    /// git failing does not suppress the report. The graph half is complete without it, and an
    /// incident is the worst moment to throw away a correct answer because a second source was
    /// unavailable. The failure is stated and the run is marked incomplete.
    /// </summary>
    [Fact]
    public void AMissingRepoDoesNotSuppressTheReport()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"flowlens-triage-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);

        try
        {
            var report = _fixture.Build("A", directory);
            var markdown = TriageMarkdown.Render(report);

            Assert.NotNull(report.ErrorPoint);
            Assert.NotNull(report.EntryPoints);
            Assert.NotEmpty(report.EntryPoints!.Groups);

            Assert.True(report.Incomplete);
            Assert.False(report.Commits.Git.Available);
            Assert.False(string.IsNullOrWhiteSpace(report.Commits.Git.Error));
            Assert.Contains("git okunamadı", markdown, StringComparison.Ordinal);

            // The stated exit code must be the one the CLI will actually return.
            Assert.Contains("çıkış kodu **3**", markdown, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The commit section states its own size, so an unbounded case is visible instead of silently enormous.</summary>
    [Fact]
    public void TheCommitSectionReportsItsOwnSize()
    {
        var report = _fixture.Build("A");

        Assert.True(report.Commits.Git.Available, report.Commits.Git.Error);
        Assert.Equal(report.Commits.Files.Count, report.Commits.FileCount);
        Assert.Equal(report.Commits.Git.Files.Sum(f => f.Commits.Count), report.Commits.CommitLines);

        Assert.Contains(
            $"`{report.Commits.FileCount}` dosya, `{report.Commits.CommitLines}` commit satırı",
            TriageMarkdown.Render(report),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// git output is asserted by SHAPE, never by content. Asserting a commit subject would make
    /// this test fail on the target's next commit - a test that breaks when nothing broke.
    /// </summary>
    [Fact]
    public void CommitsParseIntoAShaAndASubject()
    {
        var report = _fixture.Build("A");

        Assert.Matches("^[0-9a-f]{7,40}$", report.Commits.Git.Head);

        foreach (var commit in report.Commits.Git.Files.SelectMany(f => f.Commits))
        {
            Assert.Matches("^[0-9a-f]{7,40}$", commit.Sha);
        }
    }

    /// <summary>
    /// Phase 5 found a markdown lazy-continuation defect on 10 of 10 module pages: no compiler, no
    /// test and no mermaid.parse() saw it, because the file parses fine and RENDERS wrong. That was
    /// a CLASS of defect, not one line, so the same rule is applied to this phase's markdown too -
    /// the triage report is markdown emitted by a StringBuilder, exactly the shape that produced it.
    /// </summary>
    [Theory]
    [InlineData("A")]
    [InlineData("A2")]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    public void NoBlockStartsWithoutABlankLineBeforeIt(string name)
    {
        var lines = TriageMarkdown.Render(_fixture.Build(name)).Split('\n');
        var violations = new List<string>();

        for (var i = 1; i < lines.Length; i++)
        {
            if (!StartsABlock(lines[i]) || lines[i - 1].Trim().Length == 0 || Continues(lines[i], lines[i - 1]))
            {
                continue;
            }

            violations.Add($"{name}:{i + 1} \"{lines[i]}\" after \"{lines[i - 1]}\"");
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

    /// <summary>Phase 5's rule: an artefact that records its own generation time can never be byte-identical twice.</summary>
    [Theory]
    [InlineData("A")]
    [InlineData("A2")]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    public void NoReportRecordsWhenItWasGenerated(string name)
    {
        var markdown = TriageMarkdown.Render(_fixture.Build(name));

        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}", markdown);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("A2")]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    public void TheSameInputTwiceProducesTheSameBytes(string name)
    {
        Assert.Equal(
            TriageMarkdown.Render(_fixture.Build(name)),
            TriageMarkdown.Render(_fixture.Build(name)));
    }

    /// <summary>
    /// Phase 5's deeper lesson: sorting the output is not enough, the DISCOVERY has to be
    /// deterministic too. Reversing the graph's node and edge order must not change a single byte -
    /// and unlike "we ran it twice", this covers every ordering rather than the two we happened to
    /// see.
    /// </summary>
    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void TheReportIsTheSameWhateverOrderTheGraphArrivesIn(string name)
    {
        var reversed = new CodeGraph(
            [.. _fixture.Graph.Nodes.Reverse()],
            [.. _fixture.Graph.Edges.Reverse()]);

        var snapshot = new GraphSnapshot(
            _fixture.Document,
            reversed,
            _fixture.Document.Diagnostics,
            DateTime.UnixEpoch,
            0,
            DateTime.UnixEpoch,
            0);

        Assert.Equal(
            TriageMarkdown.Render(_fixture.Build(name)),
            TriageMarkdown.Render(TriageBuilder.Build(
                snapshot, TestPaths.RepositoryGraph, TriageFixture.Read(name), null)));
    }

    /// <summary>
    /// A frame is resolved to one node or reported as ambiguous - never picked by whichever the
    /// dictionary yielded first. The one genuinely unresolvable pair in this target is a pair of
    /// Consume overloads the runtime renders identically.
    /// </summary>
    [Fact]
    public void AnAmbiguousFrameIsReportedRatherThanResolved()
    {
        var matcher = new FrameMatcher(_fixture.Graph);

        var parsed = StackTraceParser.Parse(
            "System.Exception: x\n"
            + "   at ModularCommerce.Discovery.Api.Consumers.ProductChangedConsumer.Consume(ConsumeContext`1 context)"
            + " in C:\\repo\\src\\Modules\\Discovery\\X.cs:line 20\n");

        var match = matcher.Match(parsed.Frames.Single());

        Assert.Equal(FrameVerdict.Ambiguous, match.Verdict);
        Assert.True(match.Candidates.Count > 1);
        Assert.Null(match.Node);

        // Ordered, so two runs list them the same way.
        Assert.Equal(
            match.Candidates.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal),
            match.Candidates.Select(c => c.Id));
    }

    /// <summary>The project/foreign boundary comes from the graph, not from a name typed into the source.</summary>
    [Fact]
    public void ProjectNamespacesAreDerivedFromTheGraph()
    {
        var matcher = new FrameMatcher(_fixture.Graph);

        Assert.NotEmpty(matcher.ProjectPrefixes);

        foreach (var prefix in matcher.ProjectPrefixes)
        {
            Assert.Contains(_fixture.Graph.Nodes, n => n.Id.StartsWith(prefix + ".", StringComparison.Ordinal));
        }
    }

    private bool Reachable(string fromId, string toId, int maxDepth)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { fromId };
        var frontier = new List<string> { fromId };

        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();

            foreach (var target in _fixture.Graph.Edges
                .Where(e => e.Kind == EdgeKind.Calls && frontier.Contains(e.FromId, StringComparer.Ordinal))
                .Select(e => e.ToId))
            {
                if (target == toId)
                {
                    return true;
                }

                if (seen.Add(target))
                {
                    next.Add(target);
                }
            }

            frontier = next;
        }

        return false;
    }
}

/// <summary>
/// The fixtures claim to be real captures. That claim is checked here rather than trusted: a file
/// whose header says GERCEK but which was quietly edited afterwards would make every other test in
/// this file meaningless.
/// </summary>
public sealed class TriageFixtureProvenanceTests
{
    [Fact]
    public void EveryFixtureDeclaresWhereItCameFrom()
    {
        foreach (var name in TriageFixture.Names)
        {
            var first = TriageFixture.Read(name).ReplaceLineEndings("\n").Split('\n')[0];

            Assert.StartsWith("#", first, StringComparison.Ordinal);
            Assert.True(
                first.Contains("GERCEK", StringComparison.Ordinal) || first.Contains("SENTETIK", StringComparison.Ordinal),
                $"{name}: fixture basligi GERCEK ya da SENTETIK demeli, diyen: {first}");
        }
    }

    /// <summary>
    /// The acceptance criterion asks for at least two REAL traces. A synthetic one does not count
    /// towards it, so the count is asserted rather than assumed - and if a fixture is ever replaced
    /// by a hand-written one, this fails loudly instead of the criterion quietly lapsing.
    /// </summary>
    [Fact]
    public void AtLeastTwoFixturesAreRealCaptures()
    {
        var real = TriageFixture.Names
            .Count(n => TriageFixture.Read(n).StartsWith("# GERCEK", StringComparison.Ordinal));

        Assert.True(real >= 2, $"Kabul kriteri en az 2 GERCEK yigin izi ister; {real} tane var.");
    }

    /// <summary>
    /// A synthetic fixture, if one ever appears, must match the frame shape step 0a measured -
    /// otherwise the parser is only being tested against its own author's assumptions. With no
    /// synthetic fixtures this passes vacuously and says so; it does not skip.
    /// </summary>
    [Fact]
    public void ASyntheticFixtureMatchesTheMeasuredFrameShape()
    {
        var synthetic = TriageFixture.Names
            .Where(n => TriageFixture.Read(n).StartsWith("# SENTETIK", StringComparison.Ordinal))
            .ToList();

        foreach (var name in synthetic)
        {
            foreach (var line in TriageFixture.Read(name)
                .ReplaceLineEndings("\n")
                .Split('\n')
                .Where(l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal)))
            {
                Assert.Matches(
                    @"^\s+at\s+\S.*\(.*\)(\s+in\s+.+:line\s+\d+)?$",
                    line);
            }
        }

        Assert.True(true, $"{synthetic.Count} sentetik fixture var.");
    }
}
