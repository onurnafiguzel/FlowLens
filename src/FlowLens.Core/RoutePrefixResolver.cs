using Microsoft.CodeAnalysis;

namespace FlowLens.Core;

public sealed record RoutePrefixResolution(
    IReadOnlyList<EndpointRecord> Endpoints,
    IReadOnlyList<string> Warnings,
    int ReachedMethodCount);

/// <summary>
/// Pass 2 of endpoint discovery: propagate route prefixes from the roots down through every
/// method that receives a route builder, then combine them with the map-call suffixes.
/// <para>
/// This is what resolves the five-hop chain the survey documented:
/// <c>Program.cs → OrderingModule.MapEndpoints → MapOrderEndpoints → MapGroup("") → MapPost("/checkout")</c>.
/// The prefix and the suffix live in different files, so nothing local can join them.
/// </para>
/// </summary>
public static class RoutePrefixResolver
{
    /// <summary>
    /// Cap on distinct prefixes per method. A legitimate multi-mount is small; anything beyond
    /// this means the propagation is chasing something it does not understand.
    /// </summary>
    private const int MaxPrefixesPerMethod = 8;

    public static async Task<RoutePrefixResolution> ResolveAsync(
        IReadOnlyList<MapCallSite> mapCalls,
        IReadOnlyList<PropagationSite> propagations,
        ImplementationResolver implementationResolver,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();

        var mapCallsByMethod = Group(mapCalls, c => c.EnclosingMethod);
        var propagationsByMethod = Group(propagations, p => p.EnclosingMethod);

        // Accumulates every prefix a method is reached with. A method mounted under two groups
        // legitimately has two prefixes, so this is a set - never overwritten.
        var prefixesByMethod = new Dictionary<IMethodSymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        var overflowed = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        var queue = new Queue<(IMethodSymbol Method, string Prefix)>();
        var visited = new HashSet<(string Method, string Prefix)>();

        // Seed: everything whose builder is rooted needs no caller to know its prefix.
        foreach (var propagation in propagations.Where(p => p.Origin.Kind == BuilderOriginKind.Root))
        {
            foreach (var target in await TargetsOfAsync(propagation, implementationResolver, warnings, cancellationToken))
            {
                Enqueue(target, RouteText.Combine(propagation.Origin.RelativePrefix));
            }
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (method, prefix) = queue.Dequeue();

            if (!propagationsByMethod.TryGetValue(method, out var outgoing))
            {
                continue;
            }

            foreach (var propagation in outgoing.Where(p => p.Origin.Kind == BuilderOriginKind.Parameter))
            {
                var next = RouteText.Combine(prefix, propagation.Origin.RelativePrefix);

                foreach (var target in await TargetsOfAsync(propagation, implementationResolver, warnings, cancellationToken))
                {
                    Enqueue(target, next);
                }
            }
        }

        var endpoints = new List<EndpointRecord>();

        foreach (var call in mapCalls)
        {
            switch (call.Origin.Kind)
            {
                case BuilderOriginKind.Root:
                    endpoints.Add(Build(call, RouteText.Combine(call.Origin.RelativePrefix, call.RouteSuffix), multiMount: false));
                    break;

                case BuilderOriginKind.Parameter:
                {
                    if (!prefixesByMethod.TryGetValue(call.EnclosingMethod, out var prefixes) || prefixes.Count == 0)
                    {
                        warnings.Add(
                            $"{call.FilePath}:{call.Line} - no route prefix reached " +
                            $"{call.EnclosingMethod.ToDisplayString(NodeId.MemberFormat)}; route left unresolved");
                        endpoints.Add(Build(call, route: null, multiMount: false));
                        break;
                    }

                    // A2b: one endpoint node per prefix. Taking the first would silently emit a
                    // wrong route for every other mount point.
                    var multiMount = prefixes.Count > 1;
                    if (multiMount)
                    {
                        warnings.Add(
                            $"{call.FilePath}:{call.Line} - multi-mount: {prefixes.Count} prefixes " +
                            $"({string.Join(", ", prefixes.Order(StringComparer.Ordinal))})");
                    }

                    foreach (var prefix in prefixes.Order(StringComparer.Ordinal))
                    {
                        // Three parts, not two. The incoming prefix stops at the method's
                        // parameter; anything the call chained on top of it - as in
                        // endpoints.MapGroup("/api").MapPost(...) - lives on the call's own origin
                        // and would otherwise be dropped.
                        endpoints.Add(Build(
                            call,
                            RouteText.Combine(prefix, call.Origin.RelativePrefix, call.RouteSuffix),
                            multiMount));
                    }

                    break;
                }

                default:
                    warnings.Add(
                        $"{call.FilePath}:{call.Line} - route builder origin could not be traced; " +
                        "route left unresolved");
                    endpoints.Add(Build(call, route: null, multiMount: false));
                    break;
            }
        }

        foreach (var method in overflowed)
        {
            warnings.Add(
                $"{method.ToDisplayString(NodeId.MemberFormat)} - more than {MaxPrefixesPerMethod} " +
                "distinct route prefixes; propagation stopped");
        }

        return new RoutePrefixResolution(endpoints, warnings, prefixesByMethod.Count);

        void Enqueue(IMethodSymbol method, string prefix)
        {
            if (!visited.Add((method.ToDisplayString(NodeId.MemberFormat), prefix)))
            {
                return;
            }

            if (!prefixesByMethod.TryGetValue(method, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                prefixesByMethod[method] = set;
            }

            if (set.Count >= MaxPrefixesPerMethod)
            {
                overflowed.Add(method);
                return;
            }

            set.Add(prefix);
            queue.Enqueue((method, prefix));
        }
    }

    /// <summary>
    /// Where a propagation actually lands. <c>Program.cs</c> calls <c>module.MapEndpoints(app)</c>
    /// through <c>IModule</c>, so the target binds to the interface member and has to be expanded
    /// to the nine concrete module implementations before the prefix can flow anywhere.
    /// </summary>
    private static async Task<IReadOnlyList<IMethodSymbol>> TargetsOfAsync(
        PropagationSite propagation,
        ImplementationResolver resolver,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var target = propagation.Target;

        if (!ImplementationResolver.NeedsResolution(target))
        {
            return [target];
        }

        var result = await resolver.ResolveAsync(target, cancellationToken);

        if (result.Implementations.Count > 0)
        {
            return [.. result.Implementations.Select(NodeId.Canonical)];
        }

        // Falling back to the interface member itself would strand the prefix: no method body
        // declares the interface member, so propagation stops there and every endpoint below it
        // silently loses its prefix. Say so instead.
        warnings.Add(
            $"{propagation.FilePath}:{propagation.Line} - no implementation found for " +
            $"{target.ToDisplayString(NodeId.MemberFormat)}; prefix propagation stops here");

        return [];
    }

    private static EndpointRecord Build(MapCallSite call, string? route, bool multiMount)
    {
        var resolved = route is not null && call.RouteSuffix is not null;
        var displayRoute = resolved ? route! : "(unresolved)";

        return new EndpointRecord(
            Id: NodeId.ForEndpoint(call.HttpMethod, displayRoute),
            HttpMethod: call.HttpMethod,
            Route: displayRoute,
            RouteResolved: resolved,
            HandlerBody: call.HandlerBody,
            DocumentId: call.DocumentId,
            EnclosingMethod: call.EnclosingMethod,
            FilePath: call.FilePath,
            Line: call.Line,
            Module: call.Module,
            MultiMount: multiMount);
    }

    private static Dictionary<IMethodSymbol, List<T>> Group<T>(
        IReadOnlyList<T> items,
        Func<T, IMethodSymbol> keySelector)
    {
        var map = new Dictionary<IMethodSymbol, List<T>>(SymbolEqualityComparer.Default);

        foreach (var item in items)
        {
            var key = keySelector(item);
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(item);
        }

        return map;
    }
}
