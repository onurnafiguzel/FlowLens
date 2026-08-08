using FlowLens.Core;

namespace FlowLens.Tests;

/// <summary>
/// Loads the target solution and builds the whole graph once for the Phase 3 integration class.
/// Loading plus building costs tens of seconds, so doing it per test would be unusable.
/// </summary>
public sealed class Phase3Fixture : IAsyncLifetime
{
    private SolutionLoadResult _load = null!;

    public GraphBuildResult Build { get; private set; } = null!;

    public CodeGraph Graph => Build.Graph;

    public async Task InitializeAsync()
    {
        // Throws with setup instructions when unconfigured - never silently skips.
        var solutionPath = TargetSolution.Path;
        var solutionDirectory = TargetSolution.Directory;

        _load = await SolutionLoader.LoadAsync(solutionPath);

        Assert.False(_load.HasFailures, "target solution must load cleanly for Phase 3 tests to mean anything");
        Assert.True(_load.AllProjectsLoaded, "a silently skipped project would make a missing table meaningless");

        Build = await GraphBuilder.BuildAsync(_load.Solution, solutionDirectory, new TraversalOptions());

        // Phase 3 additionally needs the target BUILT, not merely present. Say so plainly here
        // rather than letting every table assertion fail with a confusing "not found".
        Assert.True(
            Build.ModelResult.Snapshots.Count > 0,
            "no EF model was read, so the graph has no tables at all. Phase 3 reads table and " +
            "column names from compiled assemblies - build the target first:" + Environment.NewLine +
            $"  dotnet build \"{solutionPath}\"" + Environment.NewLine +
            "Reported: " + string.Join(" | ", Build.ModelResult.Warnings));
    }

    public Task DisposeAsync()
    {
        _load?.Dispose();
        return Task.CompletedTask;
    }
}
