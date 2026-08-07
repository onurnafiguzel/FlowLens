namespace FlowLens.Core;

/// <param name="Module">Module name.</param>
/// <param name="PerLayer">Method count keyed by layer name; "(other)" collects projects with no known layer suffix.</param>
public sealed record ModuleBreakdown(string Module, IReadOnlyDictionary<string, int> PerLayer, int Total);

/// <summary>
/// Aggregates <see cref="MethodRecord"/>s into the per-module counts Phase 1 has to report.
/// Pure data - formatting lives in the CLI so the numbers stay testable.
/// </summary>
public sealed class ScanReport
{
    public const string OtherLayer = "(other)";

    private ScanReport(
        IReadOnlyList<ModuleBreakdown> modules,
        IReadOnlyList<ModuleBreakdown> testModules,
        int productionMethodCount,
        int testMethodCount,
        int documentCount,
        IReadOnlyList<string> skippedProjects)
    {
        Modules = modules;
        TestModules = testModules;
        ProductionMethodCount = productionMethodCount;
        TestMethodCount = testMethodCount;
        DocumentCount = documentCount;
        SkippedProjects = skippedProjects;
    }

    /// <summary>Non-test modules, alphabetical.</summary>
    public IReadOnlyList<ModuleBreakdown> Modules { get; }

    /// <summary>Test projects, reported separately so they never inflate the production picture.</summary>
    public IReadOnlyList<ModuleBreakdown> TestModules { get; }

    public int ProductionMethodCount { get; }

    public int TestMethodCount { get; }

    public int TotalMethodCount => ProductionMethodCount + TestMethodCount;

    public int DocumentCount { get; }

    public IReadOnlyList<string> SkippedProjects { get; }

    /// <summary>Layer columns actually present, in the canonical order plus "(other)" last.</summary>
    public IReadOnlyList<string> LayerColumns { get; private set; } = [];

    public static ScanReport Build(ScanResult scan)
    {
        var production = scan.Methods.Where(m => !m.IsTestProject).ToList();
        var tests = scan.Methods.Where(m => m.IsTestProject).ToList();

        var report = new ScanReport(
            BuildBreakdowns(production),
            BuildBreakdowns(tests),
            production.Count,
            tests.Count,
            scan.DocumentCount,
            scan.SkippedProjects);

        var present = production
            .Select(m => m.Layer ?? OtherLayer)
            .Distinct()
            .ToHashSet(StringComparer.Ordinal);

        report.LayerColumns =
        [
            .. ProjectClassifier.KnownLayers.Where(present.Contains),
            .. present.Contains(OtherLayer) ? new[] { OtherLayer } : []
        ];

        return report;
    }

    private static IReadOnlyList<ModuleBreakdown> BuildBreakdowns(IEnumerable<MethodRecord> methods) =>
    [
        .. methods
            .GroupBy(m => m.Module, StringComparer.Ordinal)
            .Select(group => new ModuleBreakdown(
                group.Key,
                group
                    .GroupBy(m => m.Layer ?? OtherLayer, StringComparer.Ordinal)
                    .ToDictionary(layer => layer.Key, layer => layer.Count(), StringComparer.Ordinal),
                group.Count()))
            .OrderBy(b => b.Module, StringComparer.Ordinal)
    ];
}
