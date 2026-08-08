using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowLens.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FlowLens.Tests;

/// <summary>
/// Hosts the API over a chosen graph file. Every test names its own file, so the missing-graph and
/// corrupt-graph cases are ordinary tests rather than something that has to be skipped.
/// </summary>
internal sealed class ApiHost(string graphPath) : WebApplicationFactory<FlowLens.Api.ApiMarker>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Absolute, so the test never depends on the runner's working directory.
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["FlowLens:GraphPath"] = Path.GetFullPath(graphPath) }));

        return base.CreateHost(builder);
    }
}

/// <summary>
/// The five endpoints, against the repository's real graph.json.
/// <para>
/// No solution is loaded and no build runs - that is the phase's central constraint, and hosting
/// the API in-process is what proves it: a test that took 32 seconds would mean the constraint had
/// been broken. Anchors are routes, table names and invariants; the measured counts live in
/// docs/phase-4-notes.md (working rule 7).
/// </para>
/// </summary>
public sealed class ApiTests
{
    private static readonly string RealGraph = TestPaths.RepositoryGraph;

    [Fact]
    public async Task EndpointsListsRoutesWithTheirLocation()
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/endpoints");

        var endpoints = body.GetProperty("endpoints").EnumerateArray().ToList();
        Assert.NotEmpty(endpoints);
        Assert.Equal(endpoints.Count, body.GetProperty("total").GetInt32());

        var checkout = endpoints.Single(e =>
            e.GetProperty("route").GetString() == "/api/ordering/checkout");

        Assert.Equal("POST", checkout.GetProperty("httpMethod").GetString());
        Assert.Equal("Ordering", checkout.GetProperty("module").GetString());
        Assert.True(checkout.GetProperty("line").GetInt32() > 0);
        Assert.Contains("OrderEndpoints.cs", checkout.GetProperty("filePath").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndpointsFiltersByModule()
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/endpoints?module=Ordering");

        var modules = body.GetProperty("endpoints").EnumerateArray()
            .Select(e => e.GetProperty("module").GetString())
            .Distinct()
            .ToList();

        Assert.Equal(["Ordering"], modules);
    }

    [Fact]
    public async Task TraceReachesTheDataLayerWithFileAndLineOnEveryClaim()
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(Trace("POST /api/ordering/checkout"));

        Assert.Equal("forward", body.GetProperty("direction").GetString());
        Assert.True(body.GetProperty("traversed").GetInt32() > 0);

        var tables = body.GetProperty("dataLayer").GetProperty("tables").EnumerateArray().ToList();
        Assert.Contains(tables, t => t.GetProperty("table").GetString() == "ordering.orders");

        // Attribution is the point: a claim nobody can check against the source is worth less than
        // no claim, so every table and every column carries a location.
        foreach (var table in tables)
        {
            Assert.False(string.IsNullOrWhiteSpace(table.GetProperty("location").GetString()));

            foreach (var column in table.GetProperty("columns").EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(column.GetProperty("location").GetString()));
                Assert.NotEmpty(column.GetProperty("mechanisms").EnumerateArray());
            }
        }
    }

    /// <summary>
    /// F10, fixed by the type rather than by convention. The CLI prints its data-layer block in both
    /// directions, and backward it silently means something else - the target's own columns rather
    /// than what the reaching flows write. A field that is null cannot be misread.
    /// </summary>
    [Fact]
    public async Task BackwardCarriesEntryPointsAndNoDataLayer()
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var forward = await client.GetFromJsonAsync<JsonElement>(Trace("table:ordering.orders", backward: false));
        var backward = await client.GetFromJsonAsync<JsonElement>(Trace("table:ordering.orders", backward: true));

        Assert.False(forward.TryGetProperty("entryPoints", out _));
        Assert.False(backward.TryGetProperty("dataLayer", out _));

        var groups = backward.GetProperty("entryPoints").GetProperty("groups").EnumerateArray().ToList();
        var kinds = groups.Select(g => g.GetProperty("rootKind").GetString()).ToList();

        // Measured: this table is reached by endpoints AND by the TTL sweeper. Reporting only the
        // endpoints would drop a suspect for Phase 6.
        Assert.Contains(nameof(RootKind.Endpoint), kinds);
        Assert.Contains(nameof(RootKind.BackgroundService), kinds);
    }

    [Fact]
    public async Task TablesListsEverySchemaQualifiedTable()
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/tables");

        var tables = body.GetProperty("tables").EnumerateArray().ToList();
        var orders = tables.Single(t => t.GetProperty("table").GetString() == "ordering.orders");

        Assert.Equal("ordering", orders.GetProperty("schema").GetString());
        Assert.Equal("Ordering", orders.GetProperty("module").GetString());
        Assert.Contains("W", orders.GetProperty("access").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatsReportsCountsAndTheStandingLimitations()
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/graph/stats");

        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("rootCount").GetInt32() > 0);

        // WHICH file was read. Pointing at a stale copy in another directory would otherwise look
        // perfectly healthy and be about the wrong repository.
        Assert.Equal(RealGraph, body.GetProperty("graphFilePath").GetString(), ignoreCase: true);
        Assert.NotEmpty(body.GetProperty("nodesByType").EnumerateObject());
        Assert.NotEmpty(body.GetProperty("diagnostics").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("graphFileWrittenAt").GetString()));

        var codes = body.GetProperty("knownLimitations").EnumerateArray()
            .Select(l => l.GetProperty("code").GetString())
            .ToList();

        Assert.Contains("raw-sql-out-of-scope", codes);
        Assert.Contains("column-reads-not-modelled", codes);
    }

    /// <summary>
    /// The answer must say what it could not see. A flow whose only data access is raw SQL returns
    /// no tables, and "no tables" alone is indistinguishable from "touches nothing" - which is the
    /// wrong answer, and the expensive kind.
    /// </summary>
    [Fact]
    public async Task AFlowThatUsesRawSqlSaysSoInsteadOfReturningAnEmptyList()
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(Trace("POST /api/discovery/search"));

        Assert.Empty(body.GetProperty("dataLayer").GetProperty("tables").EnumerateArray());

        var rawSql = body.GetProperty("limitations").EnumerateArray()
            .Single(l => l.GetProperty("code").GetString() == "raw-sql");

        var locations = rawSql.GetProperty("locations").EnumerateArray()
            .Select(l => l.GetString()!)
            .ToList();

        Assert.NotEmpty(locations);
        Assert.All(locations, l => Assert.Contains(".cs:", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryAnswerReportsWhatItHid()
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var hidden = await client.GetFromJsonAsync<JsonElement>(Trace("POST /api/ordering/checkout"));
        var shown = await client.GetFromJsonAsync<JsonElement>(
            Trace("POST /api/ordering/checkout") + "&includeUtility=true");

        Assert.True(hidden.GetProperty("filtered").GetProperty("utility").GetInt32() > 0);
        Assert.Equal(0, shown.GetProperty("filtered").GetProperty("utility").GetInt32());
        Assert.True(shown.GetProperty("traversed").GetInt32() > hidden.GetProperty("traversed").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(-1)]
    public async Task DepthOutsideTheRangeIsRejectedRatherThanClamped(int depth)
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var response = await client.GetAsync(Trace("POST /api/ordering/checkout") + $"&maxDepth={depth}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(depth, body.GetProperty("requested").GetInt32());
        Assert.Equal(Api.Queries.MaxAllowedDepth, body.GetProperty("max").GetInt32());
    }

    [Fact]
    public async Task ADepthLimitThatCutsTheWalkSaysSo()
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(
            Trace("POST /api/ordering/checkout") + "&maxDepth=2");

        Assert.Equal(2, body.GetProperty("maxDepth").GetInt32());
        Assert.True(body.GetProperty("truncated").GetBoolean());
        Assert.Contains(
            body.GetProperty("limitations").EnumerateArray(),
            l => l.GetProperty("code").GetString() == "truncated");
    }

    [Fact]
    public async Task TheAppliedDepthIsEchoedBack()
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(Trace("POST /api/ordering/checkout"));

        Assert.Equal(Api.Queries.DefaultMaxDepth, body.GetProperty("maxDepth").GetInt32());
    }

    /// <summary>Both spellings of the same node, because both are what a caller has to hand.</summary>
    [Theory]
    [InlineData("POST /api/ordering/checkout")]
    [InlineData("endpoint:POST /api/ordering/checkout")]
    public async Task AcceptsBothARouteAndANodeId(string selector)
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(Trace(selector));

        Assert.Equal("endpoint:POST /api/ordering/checkout", body.GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public async Task AnUnknownNodeIs404WithSuggestionsRatherThanAnEmptyResult()
    {
        using var host = new ApiHost(RealGraph);
        using var client = host.CreateClient();

        var response = await client.GetAsync(Trace("POST /api/ordering/chekout"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("didYouMean", out _));
    }

    private static string Trace(string node, bool backward = false) =>
        $"/{(backward ? "backward" : "trace")}?node={Uri.EscapeDataString(node)}";
}

/// <summary>
/// What the API does when its only input is missing or broken. These are the cases that decide
/// whether a wrong answer is possible at all, so they are ordinary tests over temporary files
/// rather than something the suite skips.
/// </summary>
public sealed class ApiMissingGraphTests
{
    [Fact]
    public async Task WithNoGraphEveryDataEndpointIs503WithThePathAndTheFix()
    {
        using var temp = new TempDirectory();
        using var host = new ApiHost(Path.Combine(temp.Path, "graph.json"));
        using var client = host.CreateClient();

        foreach (var route in new[] { "/endpoints", "/tables", "/trace?node=x", "/backward?node=x" })
        {
            var response = await client.GetAsync(route);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            // Every path tried, not just the last one.
            Assert.NotEmpty(body.GetProperty("attemptedPaths").EnumerateArray());
            Assert.Contains("flowlens build", body.GetProperty("fix").GetString()!, StringComparison.Ordinal);

            // The two directories whose disagreement causes this class of confusion.
            var diagnostics = body.GetProperty("pathDiagnostics");
            Assert.True(diagnostics.TryGetProperty("currentDirectory", out _));
            Assert.True(diagnostics.TryGetProperty("baseDirectory", out _));
        }
    }

    /// <summary>
    /// The diagnostic endpoint must outlive the thing it diagnoses: "why is everything 503?" has to
    /// be answerable over HTTP, not only in a log file.
    /// </summary>
    [Fact]
    public async Task StatsStillAnswersWithoutAGraph()
    {
        using var temp = new TempDirectory();
        using var host = new ApiHost(Path.Combine(temp.Path, "graph.json"));
        using var client = host.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/graph/stats");

        Assert.Equal("unavailable", body.GetProperty("status").GetString());
        Assert.Contains("flowlens build", body.GetProperty("loadError").GetString()!, StringComparison.Ordinal);
        Assert.NotEmpty(body.GetProperty("knownLimitations").EnumerateArray());
    }

    [Fact]
    public async Task ACorruptGraphIs503RatherThanAnEmptyAnswer()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "graph.json");
        File.WriteAllText(path, "{ \"version\": 1, \"nodes\": [ truncated");

        using var host = new ApiHost(path);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/endpoints");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

/// <summary>
/// Finding graph.json from wherever the process was started.
/// <para>
/// This was a real bug, and the fix that preceded it was wrong for an instructive reason: switching
/// from ContentRootPath to CurrentDirectory changed nothing, because
/// <c>dotnet run --project src/FlowLens.Api</c> sets BOTH to the project folder. Measured:
/// currentDirectory and contentRoot were identical, and the graph was two directories above them.
/// </para>
/// </summary>
public sealed class GraphPathResolverTests
{
    private static readonly string Root = Path.GetDirectoryName(TestPaths.RepositoryGraph)!;

    /// <summary>
    /// The two working directories that matter: the repository root, and the project folder that
    /// `dotnet run --project` actually leaves the process in.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("src/FlowLens.Api")]
    [InlineData("src/FlowLens.Api/bin")]
    public void FindsTheRepositoryGraphFromAnyWorkingDirectory(string relative)
    {
        using var _ = new WorkingDirectory(Path.Combine(Root, relative));

        var resolution = GraphPathResolver.Resolve(configured: null);

        Assert.True(resolution.Found, $"tried: {string.Join(" | ", resolution.Attempted)}");
        Assert.Equal(TestPaths.RepositoryGraph, resolution.Path, ignoreCase: true);
    }

    /// <summary>An explicit path is never second-guessed, even when the search would find another.</summary>
    [Fact]
    public void AnExplicitPathWinsOverTheSearch()
    {
        using var temp = new TempDirectory();
        using var _ = new WorkingDirectory(Root);

        var named = Path.Combine(temp.Path, "elsewhere.json");
        File.WriteAllText(named, "{}");

        var resolution = GraphPathResolver.Resolve(named);

        Assert.Equal(named, resolution.Path);
        Assert.Single(resolution.Attempted);
    }

    /// <summary>
    /// A failure has to name every path tried. One wrong path tells the reader where we looked but
    /// not why there, and "why there" is the entire question when the working directory surprises
    /// them.
    /// </summary>
    [Fact]
    public void ReportsEveryPathItTriedWhenNothingIsFound()
    {
        using var temp = new TempDirectory();
        using var _ = new WorkingDirectory(temp.Path);

        var resolution = GraphPathResolver.Resolve(configured: null, fileName: "no-such-graph.json");

        Assert.False(resolution.Found);
        Assert.True(resolution.Attempted.Count > 1);
        Assert.Contains("currentDirectory", resolution.Diagnostics.Keys);
        Assert.Contains("baseDirectory", resolution.Diagnostics.Keys);
    }

    /// <summary>The climb stops at the repository root rather than wandering into a home directory.</summary>
    [Fact]
    public void TheUpwardSearchStopsAtTheRepositoryRoot()
    {
        using var _ = new WorkingDirectory(Root);

        var resolution = GraphPathResolver.Resolve(configured: null, fileName: "no-such-graph.json");
        var parent = Path.GetDirectoryName(Root)!;

        Assert.DoesNotContain(
            resolution.Attempted,
            p => p.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !p.StartsWith(Root, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class WorkingDirectory : IDisposable
    {
        private readonly string _previous = Environment.CurrentDirectory;

        public WorkingDirectory(string path) => Environment.CurrentDirectory = path;

        public void Dispose() => Environment.CurrentDirectory = _previous;
    }
}

/// <summary>Reload behaviour, tested directly because the interesting cases are timing-shaped.</summary>
public sealed class GraphSourceTests
{
    [Fact]
    public void KeepsTheLastGoodGraphWhenAReloadFails()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "graph.json");

        WriteGraph(path, "a");
        var source = new GraphSource(path);
        Assert.NotNull(source.Refresh());

        // A build in progress leaves a half-written file. Dropping to nothing here would turn
        // "the build is running" into "this flow touches nothing".
        File.WriteAllText(path, "{ truncated");

        var after = source.Refresh();

        Assert.NotNull(after);
        Assert.NotNull(source.LoadError);
        Assert.Contains("a", after.Graph.Nodes.Select(n => n.Id));
    }

    /// <summary>
    /// Length is checked as well as write time. Timestamp resolution is filesystem-dependent, so two
    /// builds inside one tick would otherwise leave the second invisible - a stale graph served as
    /// fresh. Simulated by writing a different length within the same stamped time.
    /// </summary>
    [Fact]
    public void NoticesAChangeOfLengthEvenWhenTheTimestampIsUnchanged()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "graph.json");

        WriteGraph(path, "a");
        var stamp = File.GetLastWriteTimeUtc(path);

        var source = new GraphSource(path);
        Assert.NotNull(source.Refresh());

        WriteGraph(path, "a", "bb");
        File.SetLastWriteTimeUtc(path, stamp);

        var after = source.Refresh();

        Assert.NotNull(after);
        Assert.Contains("bb", after.Graph.Nodes.Select(n => n.Id));
        Assert.Equal(1, source.ReloadCount);
    }

    [Fact]
    public void DoesNotReloadWhenNothingChanged()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "graph.json");

        WriteGraph(path, "a");
        var source = new GraphSource(path);

        var first = source.Refresh();
        var second = source.Refresh();

        Assert.Same(first, second);
        Assert.Equal(0, source.ReloadCount);
    }

    private static void WriteGraph(string path, params string[] ids) =>
        GraphJson.Write(path, new GraphDocument(
            GraphJson.SchemaVersion,
            "x.sln",
            new GraphStats(
                new Dictionary<string, int>(), new Dictionary<string, int>(),
                new Dictionary<string, int>(), 0, 0, 0, 0),
            [.. ids.Select(id => new Node(id, NodeKind.Method, id, "M", "F.cs", 1))],
            [],
            []));
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Directory.CreateDirectory(Path);

    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"flowlens-api-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }
}
