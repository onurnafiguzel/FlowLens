using FlowLens.Core;

namespace FlowLens.Tests;

/// <summary>
/// The graph file's invariants. These are enforced in production code rather than only asserted
/// here, because the roadmap makes filePath and line mandatory for every node: an answer nobody can
/// check against the source is worth less than no answer, so an unattributable graph is not written.
/// </summary>
public sealed class GraphJsonTests
{
    [Fact]
    public void RejectsANodeWithNoFilePath()
    {
        var ex = Assert.Throws<InvalidGraphException>(() =>
            GraphJson.Validate([Node("a", SourceLocation.NoSource, 12)], []));

        Assert.Contains("filePath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsANodeWithNoLine()
    {
        var ex = Assert.Throws<InvalidGraphException>(() =>
            GraphJson.Validate([Node("a", "File.cs", 0)], []));

        Assert.Contains("line", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dangling edge silently truncates every traversal that would have crossed it, which looks
    /// exactly like "nothing is there".
    /// </summary>
    [Fact]
    public void RejectsAnEdgePointingAtAMissingNode()
    {
        var ex = Assert.Throws<InvalidGraphException>(() =>
            GraphJson.Validate([Node("a", "File.cs", 1)], [new Edge("a", "ghost", EdgeKind.Calls)]));

        Assert.Contains("ghost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotWriteAFileWhenValidationFails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flowlens-invalid-{Guid.NewGuid():N}.json");

        var document = new GraphDocument(
            GraphJson.SchemaVersion, "x.sln", Stats(), [Node("a", "File.cs", 0)], [], []);

        Assert.Throws<InvalidGraphException>(() => GraphJson.Write(path, document));
        Assert.False(File.Exists(path), "an invalid graph must not reach disk");
    }

    /// <summary>
    /// Kind is written even when it is the enum's zero value.
    /// <para>
    /// Endpoint and Calls are the defaults, so an ignore-defaults policy silently dropped the field
    /// from 25 nodes and 512 edges. Every consumer would then need to know that "no kind" means
    /// Endpoint - an unwritten rule that produces a confident misreading rather than an error.
    /// </para>
    /// </summary>
    [Fact]
    public void WritesKindEvenWhenItIsTheDefaultEnumValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flowlens-kind-{Guid.NewGuid():N}.json");

        try
        {
            GraphJson.Write(path, new GraphDocument(
                GraphJson.SchemaVersion, "x.sln", Stats(),
                [new Node("endpoint:GET /x", NodeKind.Endpoint, "GET /x", "Host", "Program.cs", 1)],
                [new Edge("endpoint:GET /x", "endpoint:GET /x", EdgeKind.Calls)],
                []));

            var json = File.ReadAllText(path);

            Assert.Contains("\"kind\": \"Endpoint\"", json, StringComparison.Ordinal);
            Assert.Contains("\"kind\": \"Calls\"", json, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A root cannot be plumbing.
    /// <para>
    /// The utility tag is structural - the declaring module is Shared - and Shared holds both real
    /// helpers and one real entry point: MigrateAndSeedHostedService, a BackgroundService that
    /// seeds catalog.products and inventory.stock_items. Tagged utility, it disappeared from four
    /// backward answers for any consumer that thins utility nodes, taking an entry point with it.
    /// Enforced here rather than left to the builder so the next root declared in Shared cannot
    /// reintroduce it silently.
    /// </para>
    /// </summary>
    [Fact]
    public void RejectsARootThatIsAlsoMarkedUtility()
    {
        var root = new Node(
            "x", NodeKind.Method, "Seeder.StartAsync", "Shared", "Seeder.cs", 1,
            Utility: true, RootKind: RootKind.BackgroundService);

        var ex = Assert.Throws<InvalidGraphException>(() => GraphJson.Validate([root], []));

        Assert.Contains("utility", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RootKind.BackgroundService), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A utility node that is NOT a root stays perfectly legal - that is L8's whole point.</summary>
    [Fact]
    public void AcceptsAUtilityNodeThatIsNotARoot() =>
        GraphJson.Validate(
            [new Node("x", NodeKind.Method, "Result.Success", "Shared", "Result.cs", 21, Utility: true)],
            []);

    /// <summary>
    /// The same graph writes the same bytes, whatever order it arrives in.
    /// <para>
    /// Stated as order-independence rather than "two consecutive builds match" because it is the
    /// stronger claim and the cheaper test: it holds for EVERY input order, not just the two a
    /// given pair of runs happened to produce - and it needs no 32-second build to check.
    /// </para>
    /// <para>
    /// Measured cause: two builds of unchanged source produced set-identical files in which 8 nodes
    /// and 40 edges had moved (SymbolFinder promises no order), turning a one-field change into a
    /// 216-line diff. Phase 3's four real bugs were found by reading that file.
    /// </para>
    /// </summary>
    [Fact]
    public void WritesTheSameBytesWhateverOrderTheGraphArrivesIn()
    {
        var nodes = new[] { Node("c", "C.cs", 3), Node("a", "A.cs", 1), Node("b", "B.cs", 2) };
        var edges = new[]
        {
            new Edge("c", "a", EdgeKind.Calls, "third"),
            new Edge("a", "b", EdgeKind.Writes, "first", Mechanism: EdgeMechanism.RowInsert),
            new Edge("a", "b", EdgeKind.Writes, "second", Mechanism: EdgeMechanism.DbSetProperty),
        };

        var forward = WriteToString(new GraphDocument(
            GraphJson.SchemaVersion, "x.sln", Stats(), nodes, edges, ["z", "a"]));

        var reversed = WriteToString(new GraphDocument(
            GraphJson.SchemaVersion, "x.sln", Stats(), [.. nodes.Reverse()], [.. edges.Reverse()], ["a", "z"]));

        Assert.Equal(forward, reversed);
    }

    /// <summary>
    /// Build duration must not reach the file: an artifact that timestamps its own build can never
    /// be reproducible, and reproducibility is what keeps the diff readable.
    /// </summary>
    [Fact]
    public void DoesNotWriteBuildDurationIntoTheFile()
    {
        var stats = new GraphStats(
            new Dictionary<string, int>(), new Dictionary<string, int>(),
            new Dictionary<string, int>(), 0, 0, 0, ElapsedMs: 32_500);

        var json = WriteToString(new GraphDocument(
            GraphJson.SchemaVersion, "x.sln", stats, [Node("a", "A.cs", 1)], [], []));

        Assert.DoesNotContain("elapsed", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("32500", json, StringComparison.Ordinal);
    }

    private static string WriteToString(GraphDocument document)
    {
        var path = Path.Combine(Path.GetTempPath(), $"flowlens-{Guid.NewGuid():N}.json");

        try
        {
            GraphJson.Write(path, document);
            return File.ReadAllText(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RoundTripsNodesEdgesAndMechanisms()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flowlens-{Guid.NewGuid():N}.json");

        try
        {
            var nodes = new[] { Node("a", "A.cs", 1), Node("table:s.t", "Cfg.cs", 13) };
            var edges = new[]
            {
                new Edge("a", "table:s.t", EdgeKind.Writes, "context.T.Add at A.cs:1",
                    Mechanism: EdgeMechanism.DbSetProperty),
            };

            GraphJson.Write(path, new GraphDocument(
                GraphJson.SchemaVersion, "x.sln", Stats(), nodes, edges, ["something omitted"]));

            var read = GraphJson.Read(path);

            Assert.Equal(nodes.Length, read.Nodes.Count);
            Assert.Equal(EdgeMechanism.DbSetProperty, read.Edges.Single().Mechanism);
            Assert.Equal("context.T.Add at A.cs:1", read.Edges.Single().Evidence);

            // Diagnostics have to survive: distinguishing "touches nothing" from "we could not
            // look" is the reason they are in the file at all.
            Assert.Single(read.Diagnostics);

            Assert.NotNull(GraphJson.ToGraph(read).Find("table:s.t"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Node Node(string id, string filePath, int line) =>
        new(id, NodeKind.Method, id, "Orders", filePath, line);

    private static GraphStats Stats() =>
        new(new Dictionary<string, int>(), new Dictionary<string, int>(),
            new Dictionary<string, int>(), 0, 0, 0, 0);
}
