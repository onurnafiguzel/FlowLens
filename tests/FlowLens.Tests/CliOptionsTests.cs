using FlowLens.Cli;
using FlowLens.Core;

namespace FlowLens.Tests;

/// <summary>
/// How <c>trace</c> chooses between its two modes.
/// <para>
/// The roadmap's acceptance criterion is literally <c>flowlens trace &lt;endpoint&gt;</c>, and that
/// form has to land on the graph - it is the one that carries tables and columns. Phase 2's live
/// walk over a solution must keep working unchanged, so the argument itself decides.
/// </para>
/// </summary>
public sealed class CliOptionsTests
{
    private const string Checkout = "POST /api/ordering/checkout";

    [Fact]
    public void BareTraceOfARouteUsesTheBuiltGraph()
    {
        var options = Parse("trace", Checkout);

        Assert.Equal(CliCommand.Trace, options.Command);
        Assert.True(options.TracesGraphFile, "a route is a node, so it must traverse the graph");
        Assert.Equal(CliOptions.DefaultGraphPath, options.GraphPath);
        Assert.Equal(Checkout, options.EndpointSelector);
        Assert.Equal(TraversalDirection.Forward, options.Direction);
    }

    [Fact]
    public void BareTraceOfANodeIdAlsoUsesTheBuiltGraph()
    {
        var options = Parse("trace", "table:ordering.orders", "--direction", "backward");

        Assert.True(options.TracesGraphFile);
        Assert.Equal("table:ordering.orders", options.EndpointSelector);
        Assert.Equal(TraversalDirection.Backward, options.Direction);
    }

    /// <summary>Phase 2's invocation must be untouched: a solution path means the live walk.</summary>
    [Fact]
    public void TraceOverASolutionStillRunsTheLiveWalk()
    {
        var options = Parse("trace", "Target.sln", "--endpoint", Checkout);

        Assert.False(options.TracesGraphFile, "a solution path selects Phase 2's live trace");
        Assert.Null(options.GraphPath);
        Assert.Equal(Checkout, options.EndpointSelector);
        Assert.EndsWith("Target.sln", options.SolutionPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitGraphPathWins()
    {
        var options = Parse("trace", Checkout, "--graph", "other.json");

        Assert.True(options.TracesGraphFile);
        Assert.Equal("other.json", options.GraphPath);
    }

    [Fact]
    public void TraceOverASolutionWithoutAnEndpointIsAUsageError()
    {
        Assert.Null(CliOptions.Parse(["trace", "Target.sln"], out var error));
        Assert.Contains("--endpoint", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TraceWithNoArgumentAtAllIsAUsageError()
    {
        Assert.Null(CliOptions.Parse(["trace"], out var error));
        Assert.Contains("needs a node", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDefaultsItsOutputPath()
    {
        var options = Parse("build", "Target.sln");

        Assert.Equal(CliCommand.Build, options.Command);
        Assert.Equal(CliOptions.DefaultGraphPath, options.GraphPath);
        Assert.False(options.TracesGraphFile);
    }

    private static CliOptions Parse(params string[] args)
    {
        var options = CliOptions.Parse(args, out var error);

        Assert.True(options is not null, $"parse failed: {error}");
        return options!;
    }
}
