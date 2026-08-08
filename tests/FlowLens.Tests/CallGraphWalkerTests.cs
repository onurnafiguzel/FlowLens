using FlowLens.Core;
using Microsoft.CodeAnalysis;
// CSharpExtensions: the base SemanticModel.GetDeclaredSymbol returns ISymbol, not IMethodSymbol.
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Tests;

public sealed class CallGraphWalkerTests
{
    [Fact]
    public async Task Mutual_recursion_terminates_and_records_each_node_once()
    {
        // A cycle: Entry -> A -> B -> A. Without a visited set this never returns.
        const string source = """
            public static class Chain
            {
                public static void Entry() { A(); }
                public static void A() { B(); }
                public static void B() { A(); }
            }
            """;

        var result = await WalkAsync(source, "Entry");

        var a = Assert.Single(result.Nodes, n => n.DisplayName == "Chain.A");
        var b = Assert.Single(result.Nodes, n => n.DisplayName == "Chain.B");

        // The cycle itself is present as an edge - it is the graph that is cyclic, not the walk.
        Assert.Contains(result.Edges, e => e.FromId == b.Id && e.ToId == a.Id);
    }

    [Fact]
    public async Task A_method_reached_by_two_paths_is_one_node_with_two_edges()
    {
        const string source = """
            public static class Chain
            {
                public static void Entry() { Left(); Right(); }
                public static void Left() { Shared(); }
                public static void Right() { Shared(); }
                public static void Shared() { }
            }
            """;

        var result = await WalkAsync(source, "Entry");

        var shared = Assert.Single(result.Nodes, n => n.DisplayName == "Chain.Shared");

        // One node, two incoming edges - this is what makes the result a graph, not a tree.
        var incoming = result.Edges.Where(e => e.ToId == shared.Id).ToList();
        Assert.Equal(2, incoming.Count);
        Assert.Equal(2, incoming.Select(e => e.FromId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Depth_limit_truncates_and_says_so()
    {
        const string source = """
            public static class Chain
            {
                public static void Entry() { L1(); }
                public static void L1() { L2(); }
                public static void L2() { L3(); }
                public static void L3() { L4(); }
                public static void L4() { }
            }
            """;

        var result = await WalkAsync(source, "Entry", new TraversalOptions(MaxDepth: 2));

        // Truncation is never silent: the node that stopped the walk is flagged.
        Assert.Contains(result.Nodes, n => n.Truncated);
        Assert.DoesNotContain(result.Nodes, n => n.DisplayName == "Chain.L4");
    }

    [Fact]
    public async Task Generic_method_instantiations_collapse_onto_one_node()
    {
        // Without OriginalDefinition normalisation each instantiation becomes its own node and
        // the visited set never converges.
        const string source = """
            public static class Chain
            {
                public static void Entry() { Echo<int>(1); Echo<string>("x"); }
                public static T Echo<T>(T value) => value;
            }
            """;

        var result = await WalkAsync(source, "Entry");

        Assert.Single(result.Nodes, n => n.DisplayName == "Chain.Echo");
    }

    private static async Task<TraceResult> WalkAsync(
        string source,
        string entryMethod,
        TraversalOptions? options = null)
    {
        using var workspace = SyntheticWorkspace.Create(source);
        await workspace.AssertCompilesAsync();

        var (_, root, model) = await workspace.OpenAsync("Source.cs");

        var entry = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == entryMethod);

        var entrySymbol = model.GetDeclaredSymbol(entry)!;

        var bridge = await DomainEventBridge.BuildAsync([workspace.Project], AppContext.BaseDirectory);
        var consumers = await ConsumerIndex.BuildAsync([workspace.Project]);

        var walker = new CallGraphWalker(
            workspace.Solution,
            AppContext.BaseDirectory,
            workspace.Resolver.Build(),
            bridge,
            consumers,
            options);

        var rootNode = new Node(
            Id: NodeId.ForMethod(entrySymbol),
            Kind: NodeKind.Method,
            DisplayName: NodeId.DisplayName(entrySymbol),
            Module: "Synthetic",
            FilePath: "Source.cs",
            Line: 1);

        return await walker.WalkAsync(rootNode, entry);
    }
}
