using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace FlowLens.Core;

/// <summary>
/// The outcome of loading a solution. Owns the underlying <see cref="MSBuildWorkspace"/>;
/// disposing this releases it, after which <see cref="Solution"/> must not be used.
/// </summary>
public sealed class SolutionLoadResult : IDisposable
{
    private readonly MSBuildWorkspace _workspace;

    internal SolutionLoadResult(
        MSBuildWorkspace workspace,
        Solution solution,
        IReadOnlyList<LoadDiagnostic> diagnostics,
        int expectedProjectCount,
        TimeSpan elapsed)
    {
        _workspace = workspace;
        Solution = solution;
        Diagnostics = diagnostics;
        ExpectedProjectCount = expectedProjectCount;
        Elapsed = elapsed;
    }

    public Solution Solution { get; }

    /// <summary>Deduplicated workspace diagnostics, most frequent first.</summary>
    public IReadOnlyList<LoadDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Project count parsed straight out of the .sln file. This is the second, independent
    /// signal: OpenSolutionAsync does NOT throw when a project fails to load, it silently
    /// skips it. Comparing against <see cref="LoadedProjectCount"/> catches skips even when
    /// no diagnostic was raised.
    /// </summary>
    public int ExpectedProjectCount { get; }

    public int LoadedProjectCount => Solution.ProjectIds.Count;

    public TimeSpan Elapsed { get; }

    /// <summary>
    /// True when at least one diagnostic was a hard failure. Deliberately does NOT fold in
    /// the project-count mismatch — callers assert on the two signals separately so a
    /// failure tells you which one tripped.
    /// </summary>
    public bool HasFailures =>
        Diagnostics.Any(d => d.Kind == WorkspaceDiagnosticKind.Failure);

    public bool AllProjectsLoaded => LoadedProjectCount == ExpectedProjectCount;

    public IReadOnlyList<LoadDiagnostic> Failures =>
        [.. Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure)];

    public IReadOnlyList<LoadDiagnostic> Warnings =>
        [.. Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Warning)];

    public void Dispose() => _workspace.Dispose();
}
