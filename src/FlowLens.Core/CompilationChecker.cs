using Microsoft.CodeAnalysis;

namespace FlowLens.Core;

public sealed record ProjectCompilationResult(string ProjectName, int ErrorCount, int WarningCount, IReadOnlyList<string> SampleErrors);

public sealed record CompilationCheckResult(
    IReadOnlyList<ProjectCompilationResult> Projects,
    TimeSpan Elapsed)
{
    public int TotalErrors => Projects.Sum(p => p.ErrorCount);

    /// <summary>
    /// Reported alongside the error count so a suspiciously fast, all-zero run can be told
    /// apart from one where binding never actually happened.
    /// </summary>
    public int TotalWarnings => Projects.Sum(p => p.WarningCount);

    public IReadOnlyList<ProjectCompilationResult> Failing =>
        [.. Projects.Where(p => p.ErrorCount > 0).OrderByDescending(p => p.ErrorCount)];
}

/// <summary>
/// Compiles every project and counts diagnostics.
/// <para>
/// Behind the <c>--check-compilation</c> flag because it is the expensive operation in Phase 1:
/// building compilations for the whole solution costs minutes, while the syntax-only scan
/// costs seconds. It answers a different question than <see cref="SolutionLoader"/> does -
/// a project can load perfectly and still not compile, and a broken compilation silently
/// degrades every SemanticModel built from it.
/// </para>
/// </summary>
public static class CompilationChecker
{
    private const int MaxSampleErrorsPerProject = 3;

    public static async Task<CompilationCheckResult> CheckAsync(
        Solution solution,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<ProjectCompilationResult>();

        foreach (var project in solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (project.Language != LanguageNames.CSharp || !project.SupportsCompilation)
            {
                continue;
            }

            progress?.Report(project.Name);

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                results.Add(new ProjectCompilationResult(project.Name, 0, 0, ["(no compilation produced)"]));
                continue;
            }

            var diagnostics = compilation.GetDiagnostics(cancellationToken);
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

            results.Add(new ProjectCompilationResult(
                project.Name,
                errors.Count,
                diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
                [.. errors.Take(MaxSampleErrorsPerProject).Select(d => d.ToString())]));
        }

        started.Stop();
        return new CompilationCheckResult(results, started.Elapsed);
    }
}
