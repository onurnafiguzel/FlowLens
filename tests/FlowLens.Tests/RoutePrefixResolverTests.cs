using FlowLens.Core;

namespace FlowLens.Tests;

/// <summary>
/// Route composition rules, exercised against real ASP.NET Core symbols in a synthetic workspace.
/// </summary>
public sealed class RoutePrefixResolverTests
{
    /// <summary>
    /// Every snippet needs a root, because a prefix only becomes absolute once it reaches a
    /// builder that sits under no group. In ModularCommerce that root is <c>app</c> in Program.cs;
    /// here it is a local from WebApplication.Create(). Without it the registration methods are
    /// unreachable and every route is correctly reported unresolved - which is what the first
    /// draft of these tests actually proved.
    /// </summary>
    private const string Bootstrap = """
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Routing;

        public static class Bootstrap
        {
            public static void Run()
            {
                var app = WebApplication.Create();
                Api.MapRoot(app);
            }
        }

        """;

    [Theory]
    [InlineData(new[] { "/api/ordering", "", "/checkout" }, "/api/ordering/checkout")]
    [InlineData(new[] { "/api/ordering", "/checkout" }, "/api/ordering/checkout")]
    [InlineData(new[] { "/api/ordering/", "/checkout" }, "/api/ordering/checkout")]
    [InlineData(new[] { "", "", "/" }, "/")]
    [InlineData(new[] { "/api", "/cart/items/{id:guid}" }, "/api/cart/items/{id:guid}")]
    public void Combine_joins_by_segment(string[] parts, string expected)
    {
        // Segment-wise joining is what makes MapGroup("") disappear without a special case, and
        // what prevents /api/ordering//checkout.
        Assert.Equal(expected, RouteText.Combine(parts));
    }

    [Fact]
    public async Task Empty_map_group_does_not_produce_a_double_slash()
    {
        var endpoints = await DiscoverAsync("""
            public static class Api
            {
                public static void MapRoot(IEndpointRouteBuilder endpoints)
                {
                    var group = endpoints.MapGroup("/api/ordering");
                    group.MapOrders();
                }

                public static void MapOrders(this IEndpointRouteBuilder builder)
                {
                    var secured = ((RouteGroupBuilder)builder).MapGroup("");
                    secured.MapPost("/checkout", () => "ok");
                }
            }
            """);

        var checkout = Assert.Single(endpoints);
        Assert.True(checkout.RouteResolved);
        Assert.Equal("/api/ordering/checkout", checkout.Route);
        Assert.DoesNotContain("//", checkout.Route, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prefix_crosses_a_separate_registration_method()
    {
        // A4: the prefix and the suffix live in different methods, so nothing local can join them.
        var endpoints = await DiscoverAsync("""
            public static class Api
            {
                public static void MapRoot(IEndpointRouteBuilder endpoints)
                    => endpoints.MapGroup("/api/cart").MapCart();

                public static void MapCart(this IEndpointRouteBuilder builder)
                    => builder.MapDelete("/items/{productId:guid}", () => "ok");
            }
            """);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal("/api/cart/items/{productId:guid}", endpoint.Route);
        Assert.Equal("DELETE", endpoint.HttpMethod);
    }

    [Fact]
    public async Task One_registration_method_mounted_twice_yields_two_endpoints()
    {
        // A2b. Taking the first prefix would emit a silently wrong route for the other mount.
        var endpoints = await DiscoverAsync("""
            public static class Api
            {
                public static void MapRoot(IEndpointRouteBuilder endpoints)
                {
                    endpoints.MapGroup("/api/v1").MapOrders();
                    endpoints.MapGroup("/api/v2").MapOrders();
                }

                public static void MapOrders(this IEndpointRouteBuilder builder)
                {
                    builder.MapPost("/checkout", () => "ok");
                }
            }
            """);

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, e => Assert.True(e.RouteResolved));
        Assert.All(endpoints, e => Assert.True(e.MultiMount));

        Assert.Equal(
            ["/api/v1/checkout", "/api/v2/checkout"],
            endpoints.Select(e => e.Route).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Non_constant_route_is_reported_unresolved_rather_than_guessed()
    {
        var endpoints = await DiscoverAsync("""
            public static class Api
            {
                private static string Dynamic() => "/whatever";

                public static void MapRoot(IEndpointRouteBuilder endpoints)
                    => endpoints.MapGroup("/api").MapGet(Dynamic(), () => "ok");
            }
            """);

        var endpoint = Assert.Single(endpoints);
        Assert.False(endpoint.RouteResolved);
        Assert.Equal("(unresolved)", endpoint.Route);
    }

    [Fact]
    public async Task Const_route_is_resolved_because_constant_value_is_used_not_a_literal_check()
    {
        var endpoints = await DiscoverAsync("""
            public static class Api
            {
                private const string Route = "/checkout";

                public static void MapRoot(IEndpointRouteBuilder endpoints)
                    => endpoints.MapGroup("/api").MapPost(Route, () => "ok");
            }
            """);

        var endpoint = Assert.Single(endpoints);
        Assert.True(endpoint.RouteResolved);
        Assert.Equal("/api/checkout", endpoint.Route);
    }

    [Fact]
    public async Task A_user_defined_map_verb_is_eliminated_and_logged_never_dropped_silently()
    {
        // A1b: a custom wrapper would otherwise remove a real endpoint with no trace of why.
        var result = await RunDiscoveryAsync("""
            public static class Api
            {
                public static void MapPost(this IEndpointRouteBuilder builder, string pattern, int marker) { }

                public static void MapRoot(IEndpointRouteBuilder endpoints)
                    => endpoints.MapPost("/custom", 1);
            }
            """);

        Assert.Empty(result.Endpoints);

        var eliminated = Assert.Single(result.Eliminated);
        Assert.Contains("resolved-to-other-type", eliminated.Reason, StringComparison.Ordinal);
        Assert.True(eliminated.Line > 0, "an eliminated candidate must carry its location");
    }

    private static async Task<IReadOnlyList<EndpointRecord>> DiscoverAsync(string apiSource) =>
        (await RunDiscoveryAsync(apiSource)).Endpoints;

    private static async Task<EndpointDiscoveryResult> RunDiscoveryAsync(string apiSource)
    {
        using var workspace = SyntheticWorkspace.Create(Bootstrap + apiSource);
        await workspace.AssertCompilesAsync();

        return await EndpointDiscovery.DiscoverAsync(
            workspace.Solution, [workspace.Project], AppContext.BaseDirectory, workspace.Resolver.Build());
    }
}
