using FlowLens.Core;
using Microsoft.CodeAnalysis;
// CSharpExtensions: the base SemanticModel.GetDeclaredSymbol returns ISymbol, not IMethodSymbol.
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Tests;

/// <summary>
/// The node id format is frozen: Phase 3 stores it in graph.json, so changing it later
/// invalidates every stored graph. These tests pin the properties that format has to have.
/// </summary>
public sealed class NodeIdTests
{
    [Fact]
    public async Task Overloads_that_differ_only_by_parameter_get_different_ids()
    {
        // This is why parameters are part of the id at all: ProductChangedConsumer declares two
        // Consume overloads separated only by their ConsumeContext<T> argument, and collapsing
        // them would merge two distinct CONSUMES edges into one.
        const string source = """
            public sealed class Consumer
            {
                public void Consume(Box<int> context) { }
                public void Consume(Box<string> context) { }
            }

            public sealed class Box<T> { }
            """;

        var symbols = await MethodsAsync(source, "Consume");

        Assert.Equal(2, symbols.Count);

        var ids = symbols.Select(NodeId.ForMethod).ToList();
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Contains("Box<", id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Id_is_stable_for_the_same_symbol()
    {
        const string source = "public static class C { public static void M(int x) { } }";

        var symbol = (await MethodsAsync(source, "M")).Single();

        Assert.Equal(NodeId.ForMethod(symbol), NodeId.ForMethod(symbol));
    }

    [Fact]
    public async Task Id_is_fully_qualified_and_carries_no_global_prefix()
    {
        const string source = """
            namespace Some.Deep.Space
            {
                public static class C { public static void M(int x) { } }
            }
            """;

        var symbol = (await MethodsAsync(source, "M")).Single();
        var id = NodeId.ForMethod(symbol);

        Assert.StartsWith("Some.Deep.Space.C.M", id, StringComparison.Ordinal);
        Assert.DoesNotContain("global::", id, StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_and_event_ids_are_prefixed_so_they_cannot_collide_with_methods()
    {
        var endpointId = NodeId.ForEndpoint("POST", "/api/ordering/checkout");

        Assert.Equal("endpoint:POST /api/ordering/checkout", endpointId);
        Assert.StartsWith(NodeId.EndpointPrefix, endpointId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Event_ids_distinguish_same_named_types_in_different_namespaces()
    {
        // Ordering.Domain.Orders.OrderPaid and Ordering.Contracts.IntegrationEvents.OrderPaid
        // share a short name but are different types. A short-name id would fuse the domain
        // event with the integration event.
        const string source = """
            namespace App.Domain { public sealed record OrderPaid(); }
            namespace App.Contracts { public sealed record OrderPaid(); }
            """;

        using var workspace = SyntheticWorkspace.Create(source);
        await workspace.AssertCompilesAsync();

        var compilation = await workspace.Project.GetCompilationAsync();
        var domain = compilation!.GetTypeByMetadataName("App.Domain.OrderPaid")!;
        var contracts = compilation.GetTypeByMetadataName("App.Contracts.OrderPaid")!;

        Assert.NotEqual(NodeId.ForEvent(domain), NodeId.ForEvent(contracts));
        Assert.Equal("event:App.Domain.OrderPaid", NodeId.ForEvent(domain));
    }

    [Fact]
    public async Task Extension_method_call_and_declaration_produce_the_same_id()
    {
        // Reduced vs unreduced form. A call site binds to the reduced method (no `this`
        // parameter) while the declaration keeps it; unnormalised, route prefixes never reach the
        // registration methods they belong to.
        const string source = """
            public static class Extensions
            {
                public static void Use(this string value) { }
                public static void Caller() { "x".Use(); }
            }
            """;

        using var workspace = SyntheticWorkspace.Create(source);
        await workspace.AssertCompilesAsync();

        var (_, root, model) = await workspace.OpenAsync("Source.cs");

        var declaration = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "Use");
        var declared = model.GetDeclaredSymbol(declaration)!;

        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var called = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;

        Assert.True(called.ReducedFrom is not null, "the call site should bind to the reduced form");
        Assert.Equal(NodeId.ForMethod(declared), NodeId.ForMethod(called));
    }

    private static async Task<IReadOnlyList<IMethodSymbol>> MethodsAsync(string source, string name)
    {
        using var workspace = SyntheticWorkspace.Create(source);
        await workspace.AssertCompilesAsync();

        var (_, root, model) = await workspace.OpenAsync("Source.cs");

        return
        [
            .. root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text == name)
                .Select(m => model.GetDeclaredSymbol(m)!)
        ];
    }
}
