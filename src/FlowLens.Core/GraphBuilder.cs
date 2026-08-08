using System.Diagnostics;
using FlowLens.Core.Ef;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Core;

/// <param name="Kind">Endpoint, Consumer or BackgroundService - reported so coverage is auditable.</param>
public sealed record GraphRoot(RootKind Kind, string Id, string DisplayName);

public sealed record GraphBuildResult(
    CodeGraph Graph,
    GraphDocument Document,
    IReadOnlyList<GraphRoot> Roots,
    TraceStats WalkStats,
    EfModelReadResult ModelResult,
    EndpointDiscoveryResult Discovery,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Builds the whole graph: walks the call chains from every entry point, then overlays the data
/// layer read from EF Core's model.
/// <para>
/// Roots are endpoints, message consumers and hosted services. Endpoints alone would leave the
/// background half of the system invisible - nothing would write ordering.outbox_messages, and a
/// backward search from a swept reservation would find no caller at all - even though those paths
/// are exactly the ones an incident lands on.
/// </para>
/// <para>
/// One walker instance serves every root. Its visited set, node table and edge list are instance
/// state, so a method reachable from two endpoints is walked once and ends up with edges from
/// both, which is what makes the result a graph rather than a pile of overlapping trees.
/// </para>
/// </summary>
public static class GraphBuilder
{
    private const string BackgroundServiceMetadataName = "Microsoft.Extensions.Hosting.BackgroundService";
    private const string HostedServiceMetadataName = "Microsoft.Extensions.Hosting.IHostedService";

    public static async Task<GraphBuildResult> BuildAsync(
        Solution solution,
        string solutionDirectory,
        TraversalOptions traversalOptions,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<string>();

        // First thing, before endpoint discovery or any walking. A version skew or an unbuilt
        // target makes the whole run pointless, and finding that out after thirty seconds of
        // traversal wastes the operator's time to tell them something knowable up front.
        var buildOutput = DbContextDiscovery.FindBuildOutput(solution, solutionDirectory);
        var beforeRead = EfPreflight.BeforeRead(buildOutput, solution.FilePath);

        if (beforeRead.Blocked)
        {
            throw new EfPreflightException(beforeRead.Message);
        }

        if (buildOutput.IsStale)
        {
            // Deliberately NOT blocking. The timestamp comparison is a heuristic, and a stale build
            // does not necessarily mean a stale model - the target is stale right now, but its
            // entity configurations are untouched, so every table and column name is unaffected.
            // Refusing to run on a heuristic would be worse than saying so plainly.
            diagnostics.Add(
                $"stale build warning: {buildOutput.HostProjectName} was built before the newest source " +
                "file, so table and column names may not match the code being analysed. Rebuild the " +
                "target to be certain.");
        }

        // Test projects stay out throughout: they declare throwaway consumers and call the generic
        // Publish<T> overload, both of which would fabricate edges (known-limitations L4).
        var projects = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp && p.SupportsCompilation)
            .Where(p => !ProjectClassifier.Classify(p.FilePath ?? p.Name, p.Name).IsTest)
            .ToList();

        var implementationResolver = new ImplementationResolver(
            solution, projects, traversalOptions.ImplementationPolicy);

        var discovery = await EndpointDiscovery.DiscoverAsync(
            solution, projects, solutionDirectory, implementationResolver);

        var bridge = await DomainEventBridge.BuildAsync(projects, solutionDirectory);
        var consumers = await ConsumerIndex.BuildAsync(projects);

        var modelResult = await ReadModelAsync(solution, solutionDirectory, buildOutput, cancellationToken);
        var model = new EfModelIndex(modelResult.Snapshots);

        var walker = new CallGraphWalker(
            solution, solutionDirectory, implementationResolver, bridge, consumers, traversalOptions);

        var roots = new List<GraphRoot>();
        TraceResult? walk = null;

        foreach (var endpoint in discovery.Endpoints)
        {
            walk = await walker.WalkAsync(endpoint, cancellationToken);
            roots.Add(new GraphRoot(RootKind.Endpoint, endpoint.Id, $"{endpoint.HttpMethod} {endpoint.Route}"));
        }

        foreach (var registration in consumers.AllRegistrations.DistinctBy(r => NodeId.ForMethod(r.ConsumeMethod)))
        {
            walk = await WalkMethodAsync(walker, registration.ConsumeMethod, solutionDirectory, cancellationToken);
            roots.Add(new GraphRoot(
                RootKind.Consumer,
                NodeId.ForMethod(registration.ConsumeMethod),
                NodeId.DisplayName(registration.ConsumeMethod)));
        }

        foreach (var entry in await FindHostedServiceEntryPointsAsync(projects, cancellationToken))
        {
            walk = await WalkMethodAsync(walker, entry, solutionDirectory, cancellationToken);
            roots.Add(new GraphRoot(
                RootKind.BackgroundService, NodeId.ForMethod(entry), NodeId.DisplayName(entry)));
        }

        if (walk is null)
        {
            throw new InvalidOperationException(
                "no roots were found: the solution produced no endpoints, consumers or hosted services.");
        }

        var nodes = walk.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = new List<Edge>(walk.Edges);

        // Stamped after the walk rather than at node creation: the walker builds every node the
        // same way and does not know which ones a root loop started from. Without this the graph
        // records that 32 roots exist but not WHICH nodes they are, so a backward answer cannot
        // tell an endpoint from a background job.
        //
        // Clearing Utility here is the load-bearing half. The utility tag is structural - the
        // declaring project's module is Shared - and that rule is right for Result.Success, but
        // Shared also declares MigrateAndSeedHostedService, which is a BackgroundService root that
        // seeds catalog.products and inventory.stock_items. Tagging it utility let a consumer that
        // thins utility nodes drop an ENTRY POINT: measured, four backward answers lost
        // "who writes this table?" evidence. A root cannot be plumbing by definition, so the role
        // overrides the module rule rather than the other way round.
        foreach (var root in roots)
        {
            if (nodes.TryGetValue(root.Id, out var node))
            {
                nodes[root.Id] = node with { RootKind = root.Kind, Utility = false };
            }
        }

        // Endpoint lambdas keyed by their node id, so the overlay can analyse the ones that reach
        // the database without going through a handler.
        var endpointBodies = discovery.Endpoints
            .Where(e => e.HandlerBody is not null)
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().HandlerBody!, StringComparer.Ordinal);

        var overlay = new DataLayerOverlay(model, solution, projects, solutionDirectory);
        await overlay.ApplyAsync(
            walk.ReachedMethods, endpointBodies, nodes, edges, diagnostics, cancellationToken);

        diagnostics.AddRange(walk.Warnings);
        diagnostics.AddRange(bridge.Warnings);

        stopwatch.Stop();

        var nodeList = nodes.Values.ToList();
        var stats = GraphJson.BuildStats(nodeList, edges, roots.Count, stopwatch.Elapsed);

        var document = new GraphDocument(
            GraphJson.SchemaVersion,
            Path.GetFileName(solution.FilePath ?? solutionDirectory),
            stats,
            nodeList,
            edges,
            [.. diagnostics.Distinct(StringComparer.Ordinal)]);

        return new GraphBuildResult(
            new CodeGraph(nodeList, edges),
            document,
            roots,
            walk.Stats,
            modelResult,
            discovery,
            document.Diagnostics);
    }

    private static async Task<TraceResult> WalkMethodAsync(
        CallGraphWalker walker,
        IMethodSymbol method,
        string solutionDirectory,
        CancellationToken cancellationToken)
    {
        var (file, line) = SourceLocation.For(method, solutionDirectory);
        var module = ModuleOf(method);

        var root = new Node(
            NodeId.ForMethod(method),
            NodeKindClassifier.Classify(method),
            NodeId.DisplayName(method),
            module,
            file,
            line,
            Utility: module == ProjectClassifier.SharedModule);

        var body = method.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellationToken))
            .FirstOrDefault();

        return await walker.WalkAsync(root, body, cancellationToken);
    }

    /// <summary>
    /// Background work is a first-class entry point, not an afterthought: the outbox dispatchers
    /// and the reservation sweeper own writes no endpoint performs.
    /// </summary>
    private static async Task<IReadOnlyList<IMethodSymbol>> FindHostedServiceEntryPointsAsync(
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken)
    {
        var entryPoints = new List<IMethodSymbol>();

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            var backgroundService = compilation.GetTypeByMetadataName(BackgroundServiceMetadataName);
            var hostedService = compilation.GetTypeByMetadataName(HostedServiceMetadataName);

            if (backgroundService is null && hostedService is null)
            {
                continue;
            }

            foreach (var type in compilation.Assembly.GlobalNamespace.AllTypes(cancellationToken))
            {
                if (type.IsAbstract || type.TypeKind != TypeKind.Class || !IsHosted(type, backgroundService, hostedService))
                {
                    continue;
                }

                entryPoints.AddRange(type
                    .GetMembers()
                    .OfType<IMethodSymbol>()
                    .Where(m => m.Name is "ExecuteAsync" or "StartAsync")
                    .Where(SourceLocation.IsInSource));
            }
        }

        return entryPoints;
    }

    private static bool IsHosted(
        INamedTypeSymbol type,
        INamedTypeSymbol? backgroundService,
        INamedTypeSymbol? hostedService)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (backgroundService is not null
                && SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, backgroundService))
            {
                return true;
            }
        }

        return hostedService is not null
            && type.AllInterfaces.Any(i =>
                SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, hostedService));
    }

    private static async Task<EfModelReadResult> ReadModelAsync(
        Solution solution,
        string solutionDirectory,
        TargetBuildOutput buildOutput,
        CancellationToken cancellationToken)
    {
        var declarations = await DbContextDiscovery.FindContextsAsync(
            solution, solutionDirectory, cancellationToken);

        var result = EfProbe.Read(buildOutput.HostAssemblyPath!, declarations, cancellationToken);

        // Anything short of a complete model stops the build. Previously each of these became a
        // diagnostic line and the run continued to exit 0 - so seven contexts out of eight produced
        // a well-formed graph that was simply missing a module's tables, with nothing in the output
        // to suggest it. That is the silent wrong answer this project exists to avoid.
        var afterRead = EfPreflight.AfterRead(result, declarations);

        if (afterRead.Blocked)
        {
            throw new EfPreflightException(afterRead.Message);
        }

        return result;
    }

    internal static string ModuleOf(ISymbol symbol)
    {
        var assembly = symbol.ContainingAssembly?.Name;
        if (assembly is null)
        {
            return ProjectClassifier.UnknownModule;
        }

        var segments = assembly.Split('.');
        return segments.Length >= 2 ? segments[1] : assembly;
    }
}

internal static class NamespaceExtensions
{
    public static IEnumerable<INamedTypeSymbol> AllTypes(
        this INamespaceSymbol ns,
        CancellationToken cancellationToken)
    {
        foreach (var member in ns.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (member)
            {
                case INamespaceSymbol nested:
                    foreach (var type in nested.AllTypes(cancellationToken))
                    {
                        yield return type;
                    }

                    break;

                case INamedTypeSymbol type:
                    yield return type;
                    break;
            }
        }
    }
}
