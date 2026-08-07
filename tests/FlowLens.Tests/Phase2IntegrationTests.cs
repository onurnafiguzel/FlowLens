using FlowLens.Core;
using Microsoft.CodeAnalysis;

namespace FlowLens.Tests;

/// <summary>
/// Phase 2 against the real target solution: endpoint discovery, interface resolution and the
/// publish bridge.
/// <para>
/// Same two rules as Phase 1. No silent skipping - a missing target solution fails loudly through
/// <see cref="TargetSolution"/>. And no hardcoded counts: the target repository keeps growing, so
/// a new module or endpoint is not a regression. Assertions name anchors that must exist and
/// invariants that must hold; the day's exact figures live in docs/phase-2-notes.md.
/// </para>
/// </summary>
public sealed class Phase2IntegrationTests(Phase2Fixture fixture) : IClassFixture<Phase2Fixture>
{
    private const string CheckoutRoute = "/api/ordering/checkout";
    private const string ProductsRoute = "/api/catalog/products";

    // ------------------------------------------------------------- endpoint discovery

    [Fact]
    public void Anchor_endpoints_are_discovered_with_their_full_route()
    {
        // These routes span the five-hop chain: Program.cs -> module -> registration method ->
        // MapGroup("") -> MapPost. Getting them right is the whole of L1.
        AssertEndpoint("POST", CheckoutRoute);
        AssertEndpoint("GET", ProductsRoute);
        AssertEndpoint("DELETE", "/api/cart/items/{productId:guid}");

        void AssertEndpoint(string method, string route) =>
            Assert.True(
                fixture.Discovery.Endpoints.Any(e =>
                    e.HttpMethod == method && e.Route == route && e.RouteResolved),
                $"{method} {route} not found. Discovered: " +
                string.Join(", ", fixture.Discovery.Endpoints.Select(e => $"{e.HttpMethod} {e.Route}")));
    }

    [Fact]
    public void No_endpoint_is_left_with_an_unresolved_route()
    {
        // An unresolved route IS a real regression: it means the prefix propagation broke.
        var unresolved = fixture.Discovery.Endpoints.Where(e => !e.RouteResolved).ToList();

        Assert.True(
            unresolved.Count == 0,
            "Unresolved routes:" + Environment.NewLine +
            string.Join(Environment.NewLine, unresolved.Select(e => $"  {e.FilePath}:{e.Line}")));
    }

    [Fact]
    public void Every_endpoint_carries_attribution_and_a_module()
    {
        Assert.NotEmpty(fixture.Discovery.Endpoints);

        Assert.All(fixture.Discovery.Endpoints, endpoint =>
        {
            Assert.False(string.IsNullOrWhiteSpace(endpoint.FilePath));
            Assert.True(endpoint.Line > 0);
            Assert.False(Path.IsPathRooted(endpoint.FilePath), "paths must be solution-relative");
            Assert.NotEqual(ProjectClassifier.UnknownModule, endpoint.Module);
        });
    }

    [Fact]
    public void Eliminated_candidates_are_reported_rather_than_dropped()
    {
        // ModularCommerce has no custom Map* wrapper today, so the expected count is zero. The
        // point of the assertion is that the list EXISTS and is inspectable - if a wrapper is
        // introduced later, the endpoint it hides shows up here instead of vanishing.
        Assert.NotNull(fixture.Discovery.Eliminated);

        Assert.All(fixture.Discovery.Eliminated, candidate =>
        {
            Assert.False(string.IsNullOrWhiteSpace(candidate.Reason));
            Assert.True(candidate.Line > 0);
        });
    }

    // ------------------------------------------------------------- interface resolution

    [Fact]
    public async Task A_decorated_interface_resolves_to_all_implementations_and_is_marked_ambiguous()
    {
        var member = fixture.FindInterfaceMember("IProductReader", "GetByIdsAsync");
        var result = await fixture.Resolver.ResolveAsync(member);

        // Decorator plus concrete: both are on the real runtime path, so "all implementations"
        // is the correct answer here rather than a fallback.
        Assert.True(result.Implementations.Count > 1, "expected the decorator and the concrete reader");
        Assert.True(result.Ambiguous);
    }

    [Fact]
    public async Task Resolution_is_cached_so_the_second_call_does_not_hit_symbol_finder()
    {
        var member = fixture.FindInterfaceMember("IOrderRepository", "GetByIdempotencyKeyAsync");

        await fixture.Resolver.ResolveAsync(member);
        var callsAfterFirst = fixture.Resolver.SymbolFinderCalls;
        var hitsAfterFirst = fixture.Resolver.CacheHits;

        await fixture.Resolver.ResolveAsync(member);

        Assert.Equal(callsAfterFirst, fixture.Resolver.SymbolFinderCalls);
        Assert.Equal(hitsAfterFirst + 1, fixture.Resolver.CacheHits);
    }

    // ------------------------------------------------------------- checkout trace

    [Fact]
    public void Checkout_chain_reaches_handler_and_repository()
    {
        var trace = fixture.CheckoutTrace;

        Assert.Contains(trace.Nodes, n => n.DisplayName == "CheckoutHandler.HandleAsync");
        Assert.Contains(trace.Nodes, n => n.Kind == NodeKind.Repository);

        // Cross-module reach: checkout calls into other modules through their Contracts.
        var modules = trace.Nodes.Select(n => n.Module).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(modules.Count > 1, $"expected several modules, got: {string.Join(", ", modules)}");
    }

    [Fact]
    public void Checkout_publishes_order_paid_and_reaches_its_consumer()
    {
        var trace = fixture.CheckoutTrace;

        var eventNode = Assert.Single(trace.Nodes, n => n.Kind == NodeKind.Event);
        Assert.Contains("OrderPaid", eventNode.Id, StringComparison.Ordinal);

        // The integration event, not the same-named domain event.
        Assert.Contains("Contracts", eventNode.Id, StringComparison.Ordinal);

        var publishes = Assert.Single(trace.Edges, e => e.Kind == EdgeKind.Publishes);
        Assert.Equal(eventNode.Id, publishes.ToId);

        // Evidence keeps the raise site and the registry mapping attributable even though the
        // domain event is not itself a node.
        Assert.NotNull(publishes.Evidence);
        Assert.Contains("raise", publishes.Evidence!, StringComparison.Ordinal);

        var consumes = Assert.Single(trace.Edges, e => e.Kind == EdgeKind.Consumes);
        Assert.Equal(eventNode.Id, consumes.FromId);
        Assert.Contains(trace.Nodes, n => n.Id == consumes.ToId && n.Module == "Notification");
    }

    [Fact]
    public void Domain_events_without_a_registry_mapping_are_classified_internal_not_broken()
    {
        // OrderCreated and OrderStatusChanged are raised but deliberately unmapped - the outbox
        // interceptor skips them. Counting them as bridge failures would report false losses.
        var internalNames = fixture.CheckoutTrace.InternalDomainEvents
            .Select(e => e.DomainEventType?.Name)
            .ToList();

        Assert.Contains("OrderCreated", internalNames);
        Assert.All(fixture.CheckoutTrace.InternalDomainEvents,
            e => Assert.Equal(BridgeStatus.InternalDomainEvent, e.Status));

        // An internal event must NOT produce a publish edge.
        Assert.DoesNotContain(
            fixture.CheckoutTrace.Edges,
            e => e.Kind == EdgeKind.Publishes && e.ToId.Contains("OrderCreated", StringComparison.Ordinal));
    }

    [Fact]
    public void Trace_terminates_and_stays_within_budget()
    {
        Assert.False(fixture.CheckoutTrace.Stats.BudgetExhausted);
        Assert.NotEmpty(fixture.CheckoutTrace.Nodes);
        Assert.NotEmpty(fixture.CheckoutTrace.Edges);
    }

    [Fact]
    public void Every_traced_node_carries_attribution()
    {
        var broken = fixture.CheckoutTrace.Nodes
            .Where(n => string.IsNullOrWhiteSpace(n.FilePath) || n.FilePath == SourceLocation.NoSource || n.Line <= 0)
            .Take(5)
            .ToList();

        Assert.True(
            broken.Count == 0,
            "nodes without attribution: " + string.Join(", ", broken.Select(n => n.DisplayName)));
    }

    // ------------------------------------------------------------- consumer index

    [Fact]
    public void A_consumer_implementing_two_consumer_interfaces_registers_both()
    {
        // ProductChangedConsumer handles ProductCreated and ProductUpdated; selecting the Consume
        // overload by name alone would bind both events to whichever was seen first.
        var registrations = fixture.ProductChangedRegistrations;

        Assert.Equal(2, registrations.Count);
        Assert.Equal(2, registrations.Select(r => r.EventType.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            2,
            registrations.Select(r => NodeId.ForMethod(r.ConsumeMethod)).Distinct(StringComparer.Ordinal).Count());
    }
}
