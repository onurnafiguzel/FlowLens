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
