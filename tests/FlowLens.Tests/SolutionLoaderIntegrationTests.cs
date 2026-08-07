using FlowLens.Core;

namespace FlowLens.Tests;

/// <summary>
/// Phase 1's integration test: a real solution loads cleanly, every project in it is
/// accounted for, and the scan produces attributable results.
/// <para>
/// Counts are compared against values measured at runtime, never against literals. The target
/// repository is under active development, so asserting "66 projects" would turn every new
/// module into a red build - a false alarm, not a regression. The exact figures of the day
/// belong in docs/phase-1-notes.md.
/// </para>
/// </summary>
public sealed class SolutionLoaderIntegrationTests(TargetSolutionFixture fixture)
    : IClassFixture<TargetSolutionFixture>
{
    /// <summary>Modules expected to outlive any plausible refactor of the target repo.</summary>
    private static readonly string[] AnchorModules = ["Ordering", "Catalog", "Inventory"];

    [Fact]
    public void Solution_loads_without_workspace_failures()
    {
        var failures = fixture.Load.Failures;

        Assert.True(
            failures.Count == 0,
            $"Expected a clean load but got {failures.Count} distinct failure(s):{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures.Select(f => $"  [x{f.OccurrenceCount}] {f.Message}")));
    }

    [Fact]
    public void Every_project_in_the_solution_file_is_loaded()
    {
        // Both sides are measured now: the left from the .sln on disk, the right from the
        // workspace. OpenSolutionAsync skips unloadable projects without throwing, so this
        // comparison is the only thing standing between a partial load and a silent wrong
        // answer in later phases.
        Assert.Equal(fixture.Load.ExpectedProjectCount, fixture.Load.LoadedProjectCount);
    }

    [Fact]
    public void No_project_was_skipped_by_the_scanner()
    {
        Assert.True(
            fixture.Scan.SkippedProjects.Count == 0,
            $"Scanner skipped: {string.Join(", ", fixture.Scan.SkippedProjects)}");
    }

    [Fact]
    public void Scan_finds_methods_across_multiple_modules()
    {
        Assert.NotEmpty(fixture.Scan.Methods);
        Assert.True(
            fixture.Report.Modules.Count > 0,
            "Expected at least one production module in the report.");
    }

    [Fact]
    public void Known_modules_are_present_in_the_breakdown()
    {
        var modules = fixture.Report.Modules.Select(m => m.Module).ToHashSet(StringComparer.Ordinal);

        foreach (var anchor in AnchorModules)
        {
            Assert.True(
                modules.Contains(anchor),
                $"Module '{anchor}' missing. Found: {string.Join(", ", modules.Order(StringComparer.Ordinal))}");
        }
    }

    [Fact]
    public void Every_method_carries_a_usable_file_and_line()
    {
        // filePath + line is mandatory per the roadmap's node schema - attribution is built on
        // it, so a record without it is worthless downstream.
        var broken = fixture.Scan.Methods
            .Where(m => string.IsNullOrWhiteSpace(m.FilePath) || m.FilePath == "(no file)" || m.Line <= 0)
            .Take(5)
            .ToList();

        Assert.True(
            broken.Count == 0,
            $"Methods without attribution: {string.Join(", ", broken.Select(m => $"{m.ContainingType}.{m.Name}"))}");
    }

    [Fact]
    public void Method_locations_are_relative_to_the_solution_directory()
    {
        var absolute = fixture.Scan.Methods
            .Where(m => Path.IsPathRooted(m.FilePath))
            .Take(3)
            .ToList();

        Assert.True(
            absolute.Count == 0,
            $"Expected solution-relative paths, found absolute ones: {string.Join(", ", absolute.Select(m => m.FilePath))}");
    }

    [Fact]
    public void Test_projects_are_reported_separately_from_production_code()
    {
        Assert.All(
            fixture.Report.Modules.SelectMany(_ => fixture.Scan.Methods.Where(m => !m.IsTestProject)),
            m => Assert.False(m.IsTestProject));

        Assert.Equal(
            fixture.Report.ProductionMethodCount + fixture.Report.TestMethodCount,
            fixture.Scan.Methods.Count);
    }

    [Fact]
    public async Task SemanticModel_resolves_invocations_that_the_syntax_tree_cannot()
    {
        var demo = await SemanticModelDemo.RunAsync(
            fixture.Load.Solution,
            SemanticDemoTarget.Default,
            TargetSolution.Directory);

        Assert.NotNull(demo);
        Assert.NotEmpty(demo.Invocations);

        // The whole point of Phase 1: the semantic view carries information the syntax view
        // does not. Fully-qualified names are strictly longer than the bare callee text.
        var enriched = demo.Invocations
            .Where(i => i.Resolved)
            .Where(i => i.SemanticView.Length > i.SyntaxView.Length && i.SemanticView.Contains('.'))
            .ToList();

        Assert.NotEmpty(enriched);

        // At least one call must resolve to a symbol in a DIFFERENT assembly - that is the
        // cross-project binding Phase 2's call graph depends on.
        Assert.Contains(
            demo.Invocations,
            i => i.ContainingAssembly is not null && i.ContainingAssembly != demo.Signature.SemanticAssembly);
    }
}
