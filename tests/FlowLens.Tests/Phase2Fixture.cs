using FlowLens.Core;
using Microsoft.CodeAnalysis;

namespace FlowLens.Tests;

/// <summary>
/// Loads the target solution once and runs discovery plus one checkout trace, shared by the whole
/// Phase 2 integration class. Loading costs ~20s, so doing it per test would be unusable.
/// </summary>
public sealed class Phase2Fixture : IAsyncLifetime
{
    private SolutionLoadResult _load = null!;

    public IReadOnlyList<Project> Projects { get; private set; } = [];

    public EndpointDiscoveryResult Discovery { get; private set; } = null!;

    public ImplementationResolver Resolver { get; private set; } = null!;

    public TraceResult CheckoutTrace { get; private set; } = null!;

    public IReadOnlyList<ConsumerRegistration> ProductChangedRegistrations { get; private set; } = [];

    public async Task InitializeAsync()
    {
        // Throws with setup instructions when unconfigured - never silently skips.
        var solutionPath = TargetSolution.Path;
        var solutionDirectory = TargetSolution.Directory;

        _load = await SolutionLoader.LoadAsync(solutionPath);

        Assert.False(_load.HasFailures, "target solution must load cleanly for Phase 2 tests to mean anything");
        Assert.True(_load.AllProjectsLoaded, "a silently skipped project would make absence of an edge meaningless");

        Projects =
        [
            .. _load.Solution.Projects
                .Where(p => p.Language == LanguageNames.CSharp && p.SupportsCompilation)
                .Where(p => !ProjectClassifier.Classify(p.FilePath ?? p.Name, p.Name).IsTest)
        ];

        Resolver = new ImplementationResolver(_load.Solution, Projects);

        Discovery = await EndpointDiscovery.DiscoverAsync(
            _load.Solution, Projects, solutionDirectory, Resolver);

        var bridge = await DomainEventBridge.BuildAsync(Projects, solutionDirectory);
        var consumers = await ConsumerIndex.BuildAsync(Projects);

        ProductChangedRegistrations = FindRegistrations(consumers, "ProductChangedConsumer");

        var checkout = Discovery.Endpoints.Single(e =>
            e is { HttpMethod: "POST", Route: "/api/ordering/checkout" });

        var walker = new CallGraphWalker(
            _load.Solution, solutionDirectory, Resolver, bridge, consumers);

        CheckoutTrace = await walker.WalkAsync(checkout);
    }

    public Task DisposeAsync()
    {
        _load?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Finds an interface member by simple names, for the resolver tests.</summary>
    public IMethodSymbol FindInterfaceMember(string interfaceName, string methodName)
    {
        var member = Projects
            .Select(p => p.GetCompilationAsync().GetAwaiter().GetResult())
            .Where(c => c is not null)
            .SelectMany(c => AllTypes(c!.Assembly.GlobalNamespace))
            .Where(t => t.TypeKind == TypeKind.Interface && t.Name == interfaceName)
            .SelectMany(t => t.GetMembers(methodName))
            .OfType<IMethodSymbol>()
            .FirstOrDefault();

        Assert.True(member is not null, $"{interfaceName}.{methodName} not found in the target solution");
        return member!;
    }

    private static IReadOnlyList<ConsumerRegistration> FindRegistrations(ConsumerIndex index, string consumerTypeName) =>
        [.. index.AllRegistrations.Where(r => r.ConsumerType.Name == consumerTypeName)];

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol root)
    {
        foreach (var member in root.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol nested:
                    foreach (var type in AllTypes(nested))
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
