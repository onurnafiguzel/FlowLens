using FlowLens.Core;

namespace FlowLens.Tests;

/// <summary>
/// Loads and scans the target solution once for the whole test class. Loading costs ~20s, so
/// doing it per test would make the suite unusable.
/// </summary>
public sealed class TargetSolutionFixture : IAsyncLifetime
{
    public string SolutionPath { get; private set; } = string.Empty;

    public SolutionLoadResult Load { get; private set; } = null!;

    public ScanResult Scan { get; private set; } = null!;

    public ScanReport Report { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Throws with setup instructions when unconfigured - never silently skips.
        SolutionPath = TargetSolution.Path;

        Load = await SolutionLoader.LoadAsync(SolutionPath);
        Scan = await MethodScanner.ScanAsync(Load.Solution, TargetSolution.Directory);
        Report = ScanReport.Build(Scan);
    }

    public Task DisposeAsync()
    {
        Load?.Dispose();
        return Task.CompletedTask;
    }
}
