using FlowLens.Core;

namespace FlowLens.Tests;

/// <summary>
/// Traversal over a hand-built graph. No Roslyn, no solution, no EF - the point is to pin the
/// search itself, including the cases the real repository happens not to contain.
/// </summary>
public sealed class GraphTraversalTests
{
    private const string Endpoint = "endpoint:POST /orders";
    private const string Handler = "Orders.Handler.Handle()";
    private const string Repository = "Orders.Repo.Add()";
    private const string Helper = "Shared.Result.Success()";
    private const string Entity = "entity:Orders.Order";
    private const string Table = "table:orders.orders";

    [Fact]
    public void ForwardReachesTheDataLayerThroughTheCallChain()
    {
        var reached = Build().Forward(Endpoint, maxDepth: 20).Select(n => n.Id).ToList();

        Assert.Contains(Table, reached);
        Assert.Contains(Entity, reached);
        Assert.Contains(Repository, reached);
    }

    [Fact]
    public void BackwardFindsTheEndpointThatReachesATable()
    {
        var reached = Build().Backward(Table, maxDepth: 20).Select(n => n.Id).ToList();

        Assert.Contains(Endpoint, reached);
        Assert.DoesNotContain(Helper, reached);
    }

    /// <summary>
    /// The depth limit has to bite on the SHORTEST path, which is the property breadth-first
    /// search buys. A depth-first walk could reach the table late and report it unreachable.
    /// </summary>
    [Fact]
    public void DepthLimitCutsTheWalkAtTheShortestPath()
    {
        var graph = Build();

        Assert.DoesNotContain(Table, graph.Forward(Endpoint, maxDepth: 2).Select(n => n.Id));
        Assert.Contains(Table, graph.Forward(Endpoint, maxDepth: 4).Select(n => n.Id));
    }

    /// <summary>
    /// The utility tag is what makes L8's shared-kernel noise optional rather than a permanent
    /// choice. Both answers have to be available from the same stored graph.
    /// </summary>
    [Fact]
    public void UtilityNodesAreIncludedByDefaultAndDroppableOnRequest()
    {
        var graph = Build();

        Assert.Contains(Helper, graph.Forward(Endpoint, 20).Select(n => n.Id));

        var lean = graph.ForwardSubgraph(Endpoint, new TraversalQuery(20, IncludeUtility: false));
        Assert.DoesNotContain(Helper, lean.Nodes.Select(n => n.Id));

        // Dropping utility must not disconnect the answer that matters.
        Assert.Contains(Table, lean.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void EdgeKindFilterNarrowsTheQuestion()
    {
        var subgraph = Build().ForwardSubgraph(
            Endpoint,
            new TraversalQuery(20, EdgeKinds: new HashSet<EdgeKind> { EdgeKind.Calls }));

        Assert.Contains(Repository, subgraph.Nodes.Select(n => n.Id));
        Assert.DoesNotContain(Entity, subgraph.Nodes.Select(n => n.Id));
    }

    /// <summary>A cycle must terminate; mutual recursion is ordinary in real code.</summary>
    [Fact]
    public void CyclesTerminate()
    {
        var nodes = new[] { Method("A"), Method("B") };
        var edges = new[]
        {
            new Edge("A", "B", EdgeKind.Calls),
            new Edge("B", "A", EdgeKind.Calls),
        };

        var reached = new CodeGraph(nodes, edges).Forward("A", maxDepth: 50);

        Assert.Equal(2, reached.Count);
    }

    [Fact]
    public void UnknownStartNodeReturnsNothingRatherThanThrowing()
    {
        Assert.Empty(Build().Forward("endpoint:GET /nope", maxDepth: 5));
    }

    private static CodeGraph Build()
    {
        Node[] nodes =
        [
            new(Endpoint, NodeKind.Endpoint, "POST /orders", "Orders", "Endpoints.cs", 10),
            Method(Handler, NodeKind.Handler),
            Method(Repository, NodeKind.Repository),
            new(Helper, NodeKind.Method, "Result.Success", "Shared", "Result.cs", 5, Utility: true),
            new(Entity, NodeKind.Entity, "Order", "Orders", "Order.cs", 11),
            new(Table, NodeKind.Table, "orders.orders", "Orders", "OrderConfiguration.cs", 13),
        ];

        Edge[] edges =
        [
            new(Endpoint, Handler, EdgeKind.Calls),
            new(Handler, Repository, EdgeKind.Calls),
            new(Handler, Helper, EdgeKind.Calls),
            new(Repository, Entity, EdgeKind.Writes, Mechanism: EdgeMechanism.DbSetProperty),
            new(Entity, Table, EdgeKind.MapsTo, Mechanism: EdgeMechanism.EfModelMapping),
        ];

        return new CodeGraph(nodes, edges);
    }

    private static Node Method(string id, NodeKind kind = NodeKind.Method) =>
        new(id, kind, id, "Orders", "File.cs", 1);
}
