using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowLens.Core;
using FlowLens.Core.Docs;

namespace FlowLens.Tests;

/// <summary>The real graph, loaded once - these tests read a file, they never build anything.</summary>
public sealed class DocsFixture
{
    public DocsFixture()
    {
        Document = GraphJson.Read(TestPaths.RepositoryGraph);
        Graph = GraphJson.ToGraph(Document);
    }

    public GraphDocument Document { get; }

    public CodeGraph Graph { get; }
}

/// <summary>
/// Edge contraction is the riskiest part of this phase: a wrongly merged edge asserts a connection
/// that does not exist, Mermaid renders it happily, and nobody notices. So it is pinned from both
/// sides - nothing may be lost, and nothing may be invented.
/// </summary>
public sealed class FlowDiagramBuilderTests : IClassFixture<DocsFixture>
{
    private readonly DocsFixture _fixture;

    public FlowDiagramBuilderTests(DocsFixture fixture) => _fixture = fixture;

    /// <summary>
    /// SOUNDNESS. Every contracted edge must compress a real path: if the diagram draws A to B,
    /// the full graph must actually get from A to B. Contraction may shorten a path; inventing one
    /// would be a silent lie the renderer cannot catch.
    /// </summary>
    [Fact]
    public void EveryContractedEdgeCompressesARealPath()
    {
        var invented = new List<string>();

        foreach (var endpoint in Endpoints())
        {
            var diagram = FlowDiagramBuilder.Build(_fixture.Graph, endpoint.Id, _fixture.Document.Diagnostics);

            foreach (var edge in diagram.Edges)
            {
                var reachable = _fixture.Graph
                    .ForwardSubgraph(edge.FromId, new TraversalQuery())
                    .Nodes
                    .Select(n => n.Id)
                    .ToHashSet(StringComparer.Ordinal);

                if (!reachable.Contains(edge.ToId))
                {
                    invented.Add($"{endpoint.DisplayName}: {edge.FromId} -> {edge.ToId}");
                }
            }
        }

        Assert.Empty(invented);
    }

    /// <summary>
    /// COMPLETENESS. Measured before contraction existed: three dev endpoints came out as two boxes
    /// and no edge, because their path runs Endpoint -> Entity -> Table and Entity is dropped by the
    /// layer filter. A floating box asserts nothing while looking like an answer.
    /// </summary>
    [Fact]
    public void NoNodeIsLeftFloating()
    {
        var floating = new List<string>();

        foreach (var endpoint in Endpoints())
        {
            var diagram = FlowDiagramBuilder.Build(_fixture.Graph, endpoint.Id, _fixture.Document.Diagnostics);

            if (diagram.Nodes.Count < 2)
            {
                // A genuinely orphan endpoint is one node and no edges, which is the right answer.
                continue;
            }

            var connected = diagram.Edges
                .SelectMany(e => new[] { e.FromId, e.ToId })
                .ToHashSet(StringComparer.Ordinal);

            floating.AddRange(diagram.Nodes
                .Where(n => !connected.Contains(n.Id))
                .Select(n => $"{endpoint.DisplayName}: {n.Id}"));
        }

        Assert.Empty(floating);
    }

    /// <summary>
    /// The pruning exception. Discovery reaches no table at all, so the rule that drops branches
    /// leading to no data would delete the raw-SQL repository - the single node that explains why
    /// the diagram is empty. Keeping it is the difference between "touches nothing" and
    /// "could not look".
    /// </summary>
    [Fact]
    public void ANodeCarryingADiagnosticSurvivesPruningAndIsMarked()
    {
        var diagram = FlowDiagramBuilder.Build(
            _fixture.Graph,
            NodeId.EndpointPrefix + "POST /api/discovery/search",
            _fixture.Document.Diagnostics);

        var rawSql = Assert.Single(diagram.Nodes, n => n.HasDiagnostic);

        Assert.Contains("ProductVectorRepository", rawSql.DisplayName, StringComparison.Ordinal);
        Assert.Empty(diagram.DataLayer.Tables);
        Assert.Contains(diagram.Limitations, l => l.Code == "raw-sql");
    }

    /// <summary>Readability is the criterion, and it is measured rather than asserted by feel.</summary>
    [Fact]
    public void EveryEndpointFitsInAReadableDiagram()
    {
        var oversized = Endpoints()
            .Select(e => (e.DisplayName, Diagram: FlowDiagramBuilder.Build(_fixture.Graph, e.Id, _fixture.Document.Diagnostics)))
            .Where(x => x.Diagram.Nodes.Count > 25)
            .Select(x => $"{x.DisplayName}: {x.Diagram.Nodes.Count}")
            .ToList();

        Assert.Empty(oversized);
    }

    /// <summary>
    /// Direction follows the widest fan-out, because that is what the measurement separated on:
    /// top-down is narrower for every diagram up to a fan of 6, and left-right for the two above it.
    /// Pinned at the two ends - checkout fans out 17 ways and must stay left-right, a plain chain
    /// must go top-down - so the rule cannot silently invert.
    /// </summary>
    [Fact]
    public void DirectionFollowsTheWidestFanOut()
    {
        var wide = FlowDiagramBuilder.Build(
            _fixture.Graph, NodeId.EndpointPrefix + "POST /api/ordering/checkout", _fixture.Document.Diagnostics);
        var narrow = FlowDiagramBuilder.Build(
            _fixture.Graph, NodeId.EndpointPrefix + "GET /api/ordering/orders", _fixture.Document.Diagnostics);

        Assert.StartsWith("```mermaid\nflowchart LR", MermaidWriter.Flow(wide).ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.StartsWith("```mermaid\nflowchart TD", MermaidWriter.Flow(narrow).ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>The known orphan. One node, no edges - and that is the answer, not a failure.</summary>
    [Fact]
    public void TheOrphanEndpointStillProducesADiagram()
    {
        var diagram = FlowDiagramBuilder.Build(
            _fixture.Graph, NodeId.EndpointPrefix + "GET /", _fixture.Document.Diagnostics);

        Assert.Single(diagram.Nodes);
        Assert.Empty(diagram.Edges);
        Assert.Contains("flowchart", MermaidWriter.Flow(diagram), StringComparison.Ordinal);
    }

    private IEnumerable<Node> Endpoints() =>
        _fixture.Graph.Nodes.Where(n => n.Kind == NodeKind.Endpoint);
}

public sealed class ModuleGraphBuilderTests : IClassFixture<DocsFixture>
{
    private readonly DocsFixture _fixture;

    public ModuleGraphBuilderTests(DocsFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Cross-module synchronous calls that go through the target's Contracts project are the
    /// legitimate shape and must not be flagged. Measured: every one of checkout's cross-module
    /// calls is of this kind.
    /// </summary>
    [Fact]
    public void ContractCallsAreNotFlaggedAsViolations()
    {
        var graph = ModuleGraphBuilder.Build(_fixture.Graph);

        var ordering = graph.Edges.Where(e => e.From == "Ordering" && e.Kind == ModuleEdgeKind.Contract).ToList();

        Assert.NotEmpty(ordering);
        Assert.All(ordering, e => Assert.NotEqual(ModuleEdgeKind.Direct, e.Kind));
    }

    /// <summary>
    /// Shared is a substrate, not a dependency worth drawing: measured at 204 of 219 cross-module
    /// edges. Drawing them makes Shared a hub joined to everything and tells the reader nothing.
    /// </summary>
    [Fact]
    public void DependenciesOnSharedAreCountedRatherThanDrawn()
    {
        var graph = ModuleGraphBuilder.Build(_fixture.Graph);

        Assert.DoesNotContain(graph.Edges, e => e.To == "Shared");
        Assert.True(graph.SharedEdgeCount > 0);
        Assert.NotEmpty(graph.SharedDependents);
    }

    /// <summary>
    /// The reverse is NOT neutral. Shared infrastructure calling into a module inverts the
    /// dependency, and both measured candidates are exactly that - the MigrateAndSeedHostedService
    /// finding, now carrying evidence.
    /// </summary>
    [Fact]
    public void AnInvertedSharedDependencyIsFlaggedWithEvidence()
    {
        var graph = ModuleGraphBuilder.Build(_fixture.Graph);

        var inverted = graph.Edges.Where(e => e.From == "Shared" && e.Kind == ModuleEdgeKind.Direct).ToList();

        Assert.NotEmpty(inverted);
        Assert.All(inverted, e => Assert.NotEmpty(e.Evidence));
        Assert.All(inverted, e => Assert.Contains(e.Evidence, x => x.Contains(".cs:", StringComparison.Ordinal)));
    }
}

public sealed class MermaidWriterTests : IClassFixture<DocsFixture>
{
    private readonly DocsFixture _fixture;

    public MermaidWriterTests(DocsFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Route labels carry {, } and : - measured, 11 of each. Those characters are syntax errors in
    /// a Mermaid identifier, so ids are generated and never derived from a label.
    /// </summary>
    [Fact]
    public void RouteParametersSurviveInLabelsAndNeverReachIdentifiers()
    {
        var diagram = FlowDiagramBuilder.Build(
            _fixture.Graph,
            NodeId.EndpointPrefix + "PUT /api/cart/items/{productId:guid}",
            _fixture.Document.Diagnostics);

        var text = MermaidWriter.Flow(diagram);

        // The module rides on the label since subgraphs were dropped, so the route has to survive
        // inside it - prefix included, braces and colon untouched.
        Assert.Contains("\"Cart · PUT /api/cart/items/{productId:guid}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("subgraph", text, StringComparison.Ordinal);

        // Identifiers are the token before the shape bracket; none of them may contain a brace.
        foreach (var line in text.Split('\n').Where(l => l.TrimStart().StartsWith('n')))
        {
            var identifier = line.Trim().Split('[', '(', ' ')[0];
            Assert.DoesNotContain('{', identifier);
            Assert.DoesNotContain('/', identifier);
        }
    }

    [Fact]
    public void AngleBracketsAreEscaped()
    {
        var diagram = FlowDiagramBuilder.Build(
            _fixture.Graph,
            NodeId.EndpointPrefix + "POST /api/discovery/search",
            _fixture.Document.Diagnostics);

        var text = MermaidWriter.Flow(diagram);

        Assert.Contains("&gt;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP -> ", text, StringComparison.Ordinal);
    }
}

public sealed class DocsSiteTests : IClassFixture<DocsFixture>
{
    private readonly DocsFixture _fixture;

    public DocsSiteTests(DocsFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Byte-identical output from the same graph, whatever order it arrived in. The stronger claim
    /// than "two runs match": it holds for every permutation, and it needs no second build.
    /// </summary>
    [Fact]
    public void TheSameGraphAlwaysProducesTheSameBytes()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();

        DocsSite.Write(_fixture.Graph, _fixture.Document.Diagnostics, new DocsRequest(first.Path));

        var shuffled = new CodeGraph(
            [.. _fixture.Document.Nodes.Reverse()],
            [.. _fixture.Document.Edges.Reverse()]);

        DocsSite.Write(shuffled, _fixture.Document.Diagnostics, new DocsRequest(second.Path));

        var differences = new List<string>();

        foreach (var file in Directory.GetFiles(first.Path, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(first.Path, file);
            var other = Path.Combine(second.Path, relative);

            if (!File.Exists(other) || !File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(other)))
            {
                differences.Add(relative);
            }
        }

        Assert.Empty(differences);
    }

    /// <summary>
    /// Nothing in the output may record when it was made: an artifact that timestamps itself can
    /// never be reproducible, which is why elapsedMs left graph.json in Phase 4.
    /// </summary>
    [Fact]
    public void NoPageRecordsWhenItWasGenerated()
    {
        using var temp = new TempDirectory();

        DocsSite.Write(_fixture.Graph, _fixture.Document.Diagnostics, new DocsRequest(temp.Path));

        var year = DateTime.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);

        foreach (var file in Directory.GetFiles(temp.Path, "*.md", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(year, File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    /// <summary>Every endpoint gets a page, including the one that reaches nothing.</summary>
    [Fact]
    public void EveryEndpointAndModuleGetsAPage()
    {
        using var temp = new TempDirectory();

        var result = DocsSite.Write(_fixture.Graph, _fixture.Document.Diagnostics, new DocsRequest(temp.Path));

        Assert.Equal(_fixture.Graph.Nodes.Count(n => n.Kind == NodeKind.Endpoint), result.Flows);
        Assert.Equal(
            _fixture.Graph.Nodes.Select(n => n.Module).Distinct(StringComparer.Ordinal).Count(),
            result.Modules);

        Assert.True(result.IndexWritten);
        Assert.Contains("modules/dependencies.md", result.Files);
    }

    /// <summary>
    /// Discovery's page must say why it has no tables. "No tables" alone is indistinguishable from
    /// "touches nothing", and only one of those is true.
    /// </summary>
    [Fact]
    public void TheRawSqlModuleSaysWhyItsTablesAreMissing()
    {
        using var temp = new TempDirectory();

        DocsSite.Write(_fixture.Graph, _fixture.Document.Diagnostics, new DocsRequest(temp.Path));

        var page = File.ReadAllText(Path.Combine(temp.Path, "modules", "Discovery.md"));

        Assert.Contains("ProductVectorRepository.cs", page, StringComparison.Ordinal);
        Assert.Contains("raw SQL", page, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every claim stays checkable: a page with no file:line is a page nobody can audit.</summary>
    [Fact]
    public void EveryPageCarriesAFileAndLineReference()
    {
        using var temp = new TempDirectory();

        DocsSite.Write(_fixture.Graph, _fixture.Document.Diagnostics, new DocsRequest(temp.Path));

        foreach (var file in Directory.GetFiles(temp.Path, "*.md", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) is "README.md" or "dependencies.md")
            {
                continue;
            }

            Assert.Matches(@"\.cs:\d+", File.ReadAllText(file));
        }
    }

    /// <summary>
    /// The markdown counterpart of the Mermaid syntax gate, and it exists because that gate cannot
    /// see this class of defect at all.
    /// <para>
    /// A block-starting line needs a blank line before it. Without one, markdown treats it as lazy
    /// continuation of whatever came before: a bold heading written after a bullet is swallowed INTO
    /// that bullet. Nothing errors, nothing warns - the file simply renders wrong. Measured once at
    /// 10 of 10 module pages, from a single missing AppendLine.
    /// </para>
    /// </summary>
    [Fact]
    public void NoBlockStartsWithoutABlankLineBeforeIt()
    {
        using var temp = new TempDirectory();

        DocsSite.Write(_fixture.Graph, _fixture.Document.Diagnostics, new DocsRequest(temp.Path));

        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(temp.Path, "*.md", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            var insideFence = false;

            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("```", StringComparison.Ordinal))
                {
                    insideFence = !insideFence;
                    continue;
                }

                if (insideFence || !StartsABlock(lines[i]) || lines[i - 1].Trim().Length == 0)
                {
                    continue;
                }

                // Rows of one table, bullets of one list and lines of one quote continue a block
                // rather than starting a new one, so no blank line is owed between them.
                if (Continues(lines[i], lines[i - 1]))
                {
                    continue;
                }

                violations.Add($"{Path.GetRelativePath(temp.Path, file)}:{i + 1} after \"{lines[i - 1]}\"");
            }
        }

        Assert.Empty(violations);
    }

    private static bool StartsABlock(string line) =>
        line.StartsWith("**", StringComparison.Ordinal)
        || line.StartsWith('#')
        || line.StartsWith("- ", StringComparison.Ordinal)
        || line.StartsWith('|')
        || line.StartsWith('>');

    private static bool Continues(string line, string previous) =>
        (line.StartsWith('|') && previous.StartsWith('|'))
        || (line.StartsWith('>') && previous.StartsWith('>'))
        || (line.StartsWith("- ", StringComparison.Ordinal)
            && (previous.StartsWith("- ", StringComparison.Ordinal) || previous.StartsWith("  ", StringComparison.Ordinal)))
        || (line.StartsWith('#') && previous.StartsWith("```", StringComparison.Ordinal));

    /// <summary>
    /// The mermaid.live link has to decode back to the very diagram printed above it. It is built
    /// by hand - zlib header, stored deflate blocks, adler32 - so "this is a valid stream carrying
    /// the right bytes" is verified here rather than assumed. A link that silently points at the
    /// wrong diagram would be worse than no link.
    /// </summary>
    [Fact]
    public void EveryFlowPageCarriesALinkThatDecodesBackToItsOwnDiagram()
    {
        using var temp = new TempDirectory();

        DocsSite.Write(_fixture.Graph, _fixture.Document.Diagnostics, new DocsRequest(temp.Path));

        foreach (var file in Directory.GetFiles(Path.Combine(temp.Path, "flows"), "*.md"))
        {
            var page = File.ReadAllText(file);
            var diagram = Regex.Match(page, "```mermaid\n(.*?)```", RegexOptions.Singleline).Groups[1].Value;
            var payload = Regex.Match(page, @"mermaid\.live/edit\#pako:([A-Za-z0-9_-]+)\)").Groups[1].Value;

            Assert.NotEqual(string.Empty, payload);

            var bytes = Convert.FromBase64String(
                payload.Replace('-', '+').Replace('_', '/').PadRight((payload.Length + 3) / 4 * 4, '='));

            using var compressed = new MemoryStream(bytes);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(zlib);

            var code = JsonDocument.Parse(reader.ReadToEnd()).RootElement.GetProperty("code").GetString();

            Assert.Equal(diagram.ReplaceLineEndings("\n"), code);
        }
    }

    /// <summary>Module pages and the dependency page get no link - the decision was flows only.</summary>
    [Fact]
    public void OnlyFlowPagesCarryTheLink()
    {
        using var temp = new TempDirectory();

        DocsSite.Write(_fixture.Graph, _fixture.Document.Diagnostics, new DocsRequest(temp.Path));

        foreach (var file in Directory.GetFiles(Path.Combine(temp.Path, "modules"), "*.md"))
        {
            Assert.DoesNotContain("mermaid.live", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A filtered run writes no index. A README built from part of the site would link to files
    /// that were never written - a broken index is worse than none.
    /// </summary>
    [Fact]
    public void AFilteredRunWritesNoIndex()
    {
        using var temp = new TempDirectory();

        var result = DocsSite.Write(
            _fixture.Graph,
            _fixture.Document.Diagnostics,
            new DocsRequest(temp.Path, Endpoint: "POST /api/ordering/checkout"));

        Assert.Equal(1, result.Flows);
        Assert.False(result.IndexWritten);
        Assert.False(File.Exists(Path.Combine(temp.Path, "README.md")));
    }
}

/// <summary>
/// Runs the generated Mermaid through the same library GitHub renders it with.
/// <para>
/// Optional by design: <c>dotnet build</c> and <c>flowlens docs</c> never touch Node. But optional
/// must not mean silent - if the toolchain is missing this test FAILS with the command that fixes
/// it, because a verification that quietly does not run is worse than no verification.
/// </para>
/// </summary>
public sealed class MermaidSyntaxTests : IClassFixture<DocsFixture>
{
    private readonly DocsFixture _fixture;

    public MermaidSyntaxTests(DocsFixture fixture) => _fixture = fixture;

    [Fact]
    public void EveryGeneratedDiagramParses()
    {
        using var temp = new TempDirectory();

        DocsSite.Write(_fixture.Graph, _fixture.Document.Diagnostics, new DocsRequest(temp.Path));

        var checker = Path.Combine(
            Path.GetDirectoryName(TestPaths.RepositoryGraph)!, "tools", "mermaid-check");

        Assert.True(
            Directory.Exists(Path.Combine(checker, "node_modules")),
            $"""
             The Mermaid syntax gate could not run, so NO diagram was verified.

               expected : {Path.Combine(checker, "node_modules")}

             Run:
               cd tools/mermaid-check && npm ci
             """);

        var (exitCode, output) = Run("node", ["check.mjs", temp.Path], checker);

        Assert.True(exitCode == 0, output);
        Assert.Contains("mermaid blocks parsed", output, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output) Run(string file, string[] arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info)!;
            var text = new StringBuilder();
            text.Append(process.StandardOutput.ReadToEnd());
            text.Append(process.StandardError.ReadToEnd());
            process.WaitForExit();

            return (process.ExitCode, text.ToString());
        }
        catch (Exception ex)
        {
            // Node missing is a legitimate environment, but it must be reported, never skipped.
            throw new InvalidOperationException(
                $"""
                 The Mermaid syntax gate could not run, so NO diagram was verified.

                   command : {file} {string.Join(' ', arguments)}
                   reason  : {ex.Message}

                 Install Node 20+ and run:
                   cd tools/mermaid-check && npm ci
                 """,
                ex);
        }
    }
}
