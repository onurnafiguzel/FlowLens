using System.Diagnostics;
using FlowLens.Core;
using Microsoft.CodeAnalysis;

namespace FlowLens.Cli;

/// <summary>Phase 2 CLI surface: endpoint discovery and single-endpoint call-chain tracing.</summary>
public static class Phase2Commands
{
    /// <summary>
    /// Module endpoints counted by hand during the survey. Printed for comparison only - never
    /// asserted, because ModularCommerce keeps growing and a new module is not a regression.
    /// The total will legitimately exceed it: Program.cs and the health endpoints are real too.
    /// </summary>
    private const int SurveyModuleEndpointBaseline = 24;

    public static async Task<int> RunAsync(
        SolutionLoadResult loadResult,
        string solutionDirectory,
        CliOptions options)
    {
        var solution = loadResult.Solution;

        // Test projects are excluded throughout: they declare throwaway consumers and call the
        // generic Publish<T> overload, both of which would fabricate edges (known-limitations L4).
        var projects = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp && p.SupportsCompilation)
            .Where(p => !ProjectClassifier.Classify(p.FilePath ?? p.Name, p.Name).IsTest)
            .ToList();

        Console.WriteLine($"[3/4] Endpoint discovery ({projects.Count} non-test projects)");

        var implementationResolver = new ImplementationResolver(
            solution, projects, options.ImplementationPolicy);

        var discovery = await EndpointDiscovery.DiscoverAsync(
            solution, projects, solutionDirectory, implementationResolver);

        ReportEndpoints(discovery);

        if (options.Command == CliCommand.Endpoints)
        {
            return discovery.UnresolvedRouteCount > 0 ? Runner.ExitIncomplete : Runner.ExitOk;
        }

        var endpoint = SelectEndpoint(discovery.Endpoints, options.EndpointSelector!);
        if (endpoint is null)
        {
            ReportNoMatch(discovery.Endpoints, options.EndpointSelector!);
            return Runner.ExitNotFound;
        }

        return await TraceAsync(
            solution, projects, solutionDirectory, implementationResolver, endpoint, options);
    }

    // ---------------------------------------------------------------- endpoints

    private static void ReportEndpoints(EndpointDiscoveryResult discovery)
    {
        Console.WriteLine($"      Discovered in {discovery.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine();

        if (discovery.Endpoints.Count > 0)
        {
            var routeWidth = Math.Min(56, Math.Max(20, discovery.Endpoints.Max(e => e.Route.Length)));
            var moduleWidth = Math.Max(8, discovery.Endpoints.Max(e => e.Module.Length));

            foreach (var endpoint in discovery.Endpoints
                .OrderBy(e => e.Module, StringComparer.Ordinal)
                .ThenBy(e => e.Route, StringComparer.Ordinal)
                .ThenBy(e => e.HttpMethod, StringComparer.Ordinal))
            {
                var flags = endpoint.MultiMount ? "  [multi-mount]" : string.Empty;
                Console.WriteLine(
                    $"      {endpoint.HttpMethod,-6}  {Pad(endpoint.Route, routeWidth)}  " +
                    $"{Pad(endpoint.Module, moduleWidth)}  {endpoint.FilePath}:{endpoint.Line}{flags}");
            }

            Console.WriteLine();
        }

        Console.WriteLine(
            $"      {discovery.Endpoints.Count} endpoints · " +
            $"{discovery.UnresolvedRouteCount} unresolved route · " +
            $"{discovery.Eliminated.Count} candidates eliminated · " +
            $"{discovery.MultiMounted.Count} multi-mount");
        Console.WriteLine(
            $"      (survey baseline for module endpoints: {SurveyModuleEndpointBaseline}; " +
            "Program.cs and health endpoints are additional)");
        Console.WriteLine(
            $"      pass 1: {discovery.MapCallCount} map calls, {discovery.PropagationCount} prefix propagations · " +
            $"pass 2: {discovery.ReachedMethodCount} methods reached");

        if (discovery.Eliminated.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("      Eliminated candidates - name matched a Map verb but the symbol did not:");
            foreach (var candidate in discovery.Eliminated)
            {
                Console.WriteLine($"        {candidate.FilePath}:{candidate.Line}  {candidate.CallText}  ({candidate.Reason})");
            }
        }

        if (discovery.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("      Warnings:");
            foreach (var warning in discovery.Warnings)
            {
                Console.WriteLine($"        {warning}");
            }
        }

        Console.WriteLine();
    }

    private static EndpointRecord? SelectEndpoint(IReadOnlyList<EndpointRecord> endpoints, string selector)
    {
        var normalized = Normalize(selector);

        return endpoints.FirstOrDefault(e => Normalize($"{e.HttpMethod} {e.Route}") == normalized)
            ?? endpoints.FirstOrDefault(e => Normalize(e.Route) == normalized);
    }

    private static void ReportNoMatch(IReadOnlyList<EndpointRecord> endpoints, string selector)
    {
        Console.Error.WriteLine($"error: no endpoint matches \"{selector}\".");

        var needle = Normalize(selector);
        var near = endpoints
            .Where(e => Normalize($"{e.HttpMethod} {e.Route}").Contains(needle, StringComparison.Ordinal)
                || needle.Contains(Normalize(e.Route), StringComparison.Ordinal))
            .Take(10)
            .ToList();

        if (near.Count == 0)
        {
            return;
        }

        Console.Error.WriteLine("Did you mean:");
        foreach (var endpoint in near)
        {
            Console.Error.WriteLine($"  {endpoint.HttpMethod} {endpoint.Route}");
        }
    }

    private static string Normalize(string value) =>
        value.Trim().ToUpperInvariant().Replace("  ", " ", StringComparison.Ordinal);

    // ---------------------------------------------------------------- trace

    private static async Task<int> TraceAsync(
        Solution solution,
        IReadOnlyList<Project> projects,
        string solutionDirectory,
        ImplementationResolver implementationResolver,
        EndpointRecord endpoint,
        CliOptions options)
    {
        Console.WriteLine($"[4/4] Tracing {endpoint.HttpMethod} {endpoint.Route}");
        Console.WriteLine($"      from {endpoint.FilePath}:{endpoint.Line}");

        var setup = Stopwatch.StartNew();
        var bridge = await DomainEventBridge.BuildAsync(projects, solutionDirectory);
        var consumers = await ConsumerIndex.BuildAsync(projects);
        setup.Stop();

        Console.WriteLine(
            $"      Messaging model: {bridge.MappingCount} domain→integration mappings, " +
            $"{consumers.RegistrationCount} consumer registrations ({setup.Elapsed.TotalSeconds:F1}s)");
        Console.WriteLine();

        var walker = new CallGraphWalker(
            solution, solutionDirectory, implementationResolver, bridge, consumers,
            new TraversalOptions(MaxDepth: options.MaxDepth, ImplementationPolicy: options.ImplementationPolicy));

        var result = await walker.WalkAsync(endpoint);

        PrintTree(result, endpoint.Id);
        PrintSummary(result, bridge);

        return result.Stats.BudgetExhausted ? Runner.ExitIncomplete : Runner.ExitOk;
    }

    private static void PrintTree(TraceResult result, string rootId)
    {
        foreach (var line in TraceReport.BuildTree(result, rootId))
        {
            var indent = new string(' ', 6 + (line.Indent * 2));
            var arrow = line.IncomingKind switch
            {
                EdgeKind.Calls => "-> ",
                EdgeKind.Publishes => "=> PUBLISHES ",
                EdgeKind.Consumes => "=> CONSUMES  ",
                _ => string.Empty,
            };

            var flags = string.Concat(
                line.AmbiguousEdge ? "  [ambiguous]" : string.Empty,
                line.Node.Truncated ? "  [truncated]" : string.Empty,
                line.Repeated ? "  [seen above]" : string.Empty);

            Console.WriteLine(
                $"{indent}{arrow}{line.Node.DisplayName}  ({line.Node.Kind}, {line.Node.Module})  " +
                $"{line.Node.Location}{flags}");

            if (line.Evidence is not null)
            {
                Console.WriteLine($"{indent}   evidence: {line.Evidence}");
            }
        }

        Console.WriteLine();
    }

    private static void PrintSummary(TraceResult result, DomainEventBridge bridge)
    {
        var kinds = string.Join(", ", TraceReport.CountByKind(result).Select(k => $"{k.Kind} {k.Count}"));

        var stats = result.Stats;

        Console.WriteLine(
            $"      {stats.NodeCount} nodes ({kinds}) · {stats.EdgeCount} edges · " +
            $"max depth {stats.MaxDepthReached} · {stats.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine(
            $"      SymbolFinder: {stats.SymbolFinderCalls} calls, " +
            $"{stats.ImplementationCacheHits} cache hits");

        // Work counters. A graph with no unresolved calls proves nothing unless you also know how
        // many calls were examined to get there.
        Console.WriteLine();
        Console.WriteLine("      Invocations:");
        Console.WriteLine($"        examined            : {stats.InvocationsExamined}");
        Console.WriteLine($"        resolved by symbol  : {stats.ResolvedBySymbol}");
        Console.WriteLine($"        from candidates     : {stats.ResolvedFromCandidates}");
        Console.WriteLine($"        unresolved          : {stats.Unresolved}");
        Console.WriteLine($"        framework-filtered  : {stats.FrameworkFiltered}  (bound, but declared outside the solution)");
        Console.WriteLine($"        interface calls     : {stats.InterfaceCalls}");
        Console.WriteLine($"        ambiguous nodes     : {stats.AmbiguousNodes}");
        Console.WriteLine($"        truncated nodes     : {stats.TruncatedNodes}");

        if (stats.CandidateReasons is { Count: > 0 })
        {
            Console.WriteLine(
                "        candidate reasons   : " +
                string.Join(", ", stats.CandidateReasons.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        if (stats.NodesByDepth is { Count: > 0 })
        {
            Console.WriteLine();
            Console.WriteLine("      Nodes by depth (Phase 3 will size traversal from this):");
            foreach (var (depth, count) in stats.NodesByDepth.OrderBy(kv => kv.Key))
            {
                Console.WriteLine($"        {depth,3} : {new string('#', Math.Min(count, 60)),-60} {count}");
            }
        }

        if (result.InternalDomainEvents.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                "      Internal domain events - raised but not mapped to an integration event.");
            Console.WriteLine(
                "      This is by design, not a broken bridge: the outbox interceptor skips them.");

            foreach (var raised in result.InternalDomainEvents
                .DistinctBy(r => r.DomainEventType?.Name ?? r.RaiseSite))
            {
                Console.WriteLine($"        {raised.DomainEventType?.Name ?? "(unknown)"}  {raised.RaiseSite}");
            }
        }

        var warnings = result.Warnings.Concat(bridge.Warnings).ToList();
        if (warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("      Warnings:");
            foreach (var warning in warnings.Distinct(StringComparer.Ordinal))
            {
                Console.WriteLine($"        {warning}");
            }
        }

        Console.WriteLine();
    }

    private static string Pad(string value, int width) =>
        value.Length >= width ? value : value.PadRight(width);
}
