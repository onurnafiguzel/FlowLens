using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace FlowLens.Core;

/// <summary>
/// Loads a C# solution through <see cref="MSBuildWorkspace"/> and collects every signal that
/// says "something did not load correctly".
/// <para>
/// IMPORTANT - assembly load ordering. This is the only type in FlowLens.Core that touches
/// MSBuild types, and nothing may reference it before
/// <c>MSBuildLocator.RegisterDefaults()</c> has run. The reason is JIT timing, not statement
/// order: the CLR JIT-compiles a method when it is first called, and compiling it resolves
/// every type named in its body. So this does NOT work:
/// </para>
/// <code>
/// MSBuildLocator.RegisterDefaults();      // never gets a chance to run
/// var ws = MSBuildWorkspace.Create();     // type resolved while Main is being JIT'ed
/// </code>
/// <para>
/// Entering the enclosing method already forces the load of
/// Microsoft.CodeAnalysis.Workspaces.MSBuild, which in turn wants Microsoft.Build.*. Those
/// assemblies ship with the .NET SDK rather than the NuGet package, and the only thing that
/// teaches the runtime where to find them is the AssemblyResolve handler installed by
/// RegisterDefaults - which has not run yet. Result: FileNotFoundException.
/// </para>
/// <para>
/// The fix is separation by method boundary, not by line: register in Program.cs, then call
/// into a separate, non-inlined method that is free to name MSBuild types.
/// </para>
/// </summary>
public static class SolutionLoader
{
    public static async Task<SolutionLoadResult> LoadAsync(
        string solutionPath,
        IProgress<ProjectLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException($"Solution file not found: {solutionPath}", solutionPath);
        }

        var expectedProjectCount = CountProjectsInSolutionFile(solutionPath);

        var workspace = MSBuildWorkspace.Create();

        // Subscribe BEFORE opening. This is a live event with no replay buffer: a handler
        // attached after OpenSolutionAsync sees nothing, and the load looks clean when it was
        // not. RegisterWorkspaceFailedHandler replaces the obsolete WorkspaceFailed event and
        // hands back a registration to dispose.
        var collector = new DiagnosticCollector();
        var registration = workspace.RegisterWorkspaceFailedHandler(collector.OnWorkspaceFailed);

        var stopwatch = Stopwatch.StartNew();
        Solution solution;
        try
        {
            solution = await workspace.OpenSolutionAsync(solutionPath, progress, cancellationToken);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
        finally
        {
            stopwatch.Stop();
            registration.Dispose();
        }

        return new SolutionLoadResult(
            workspace,
            solution,
            collector.Snapshot(),
            expectedProjectCount,
            stopwatch.Elapsed);
    }

    /// <summary>
    /// Counts C# project entries in a .sln file so the caller can detect silently skipped
    /// projects. Solution folders are also written as <c>Project(...)</c> lines, so matching
    /// on the .csproj path is what separates real projects from folders.
    /// </summary>
    internal static int CountProjectsInSolutionFile(string solutionPath)
    {
        var extension = Path.GetExtension(solutionPath);

        // .slnx is XML: <Project Path="src/Foo/Foo.csproj" />
        // .sln is the classic format: Project("{GUID}") = "Foo", "src\Foo\Foo.csproj", "{GUID}"
        // Both are covered by "a line that mentions a .csproj", which also skips folder
        // entries in the classic format because those carry no project path.
        _ = extension;

        return File.ReadLines(solutionPath)
            .Count(line => line.Contains(".csproj", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DiagnosticCollector
    {
        private readonly Dictionary<(WorkspaceDiagnosticKind Kind, string Message), int> _counts = [];
        private readonly Lock _gate = new();

        // MSBuildWorkspace raises this from its own worker threads while projects load.
        public void OnWorkspaceFailed(WorkspaceDiagnosticEventArgs e)
        {
            var key = (e.Diagnostic.Kind, e.Diagnostic.Message);
            lock (_gate)
            {
                _counts[key] = _counts.GetValueOrDefault(key) + 1;
            }
        }

        public IReadOnlyList<LoadDiagnostic> Snapshot()
        {
            lock (_gate)
            {
                return
                [
                    .. _counts
                        .OrderByDescending(kv => kv.Key.Kind == WorkspaceDiagnosticKind.Failure)
                        .ThenByDescending(kv => kv.Value)
                        .Select(kv => new LoadDiagnostic(kv.Key.Kind, kv.Key.Message, kv.Value))
                ];
            }
        }
    }
}
