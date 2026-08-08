using System.Runtime.CompilerServices;
using FlowLens.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace FlowLens.Cli;

public static class Runner
{
    public const int ExitOk = 0;
    public const int ExitLoadProblem = 1;
    public const int ExitCompilationErrors = 2;

    /// <summary>Analysis ran but is knowingly incomplete (unresolved routes, budget exhausted).</summary>
    public const int ExitIncomplete = 3;

    /// <summary>The requested endpoint does not exist.</summary>
    public const int ExitNotFound = 4;

    /// <summary>
    /// A graph was produced but violates an invariant - a node without filePath or line, or an edge
    /// pointing nowhere. Not written, because an unattributable graph cannot be checked by hand.
    /// </summary>
    public const int ExitInvalidGraph = 5;

    /// <summary>
    /// The EF model could not be read or could not be trusted, so no graph was produced. Distinct
    /// from <see cref="ExitIncomplete"/> because this is an environment problem with a known fix -
    /// build the target, align a package version - not an analysis that came up short.
    /// </summary>
    public const int ExitModelUnavailable = 6;

    public const int ExitUsage = 64;

    /// <summary>
    /// NoInlining is load-bearing, not decoration. If the JIT inlined this body into the
    /// top-level Main, the MSBuild types named here would be resolved while Main is compiled -
    /// that is, before MSBuildLocator.RegisterDefaults() has executed - and the process would
    /// die with a FileNotFoundException for Microsoft.Build.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<int> RunAsync(string[] args)
    {
        var options = CliOptions.Parse(args, out var error);
        if (options is null)
        {
            if (error is not null)
            {
                Console.Error.WriteLine($"error: {error}");
                Console.Error.WriteLine();
            }

            Console.Error.WriteLine(CliOptions.Usage);
            return error is null && (args.Length == 0 || args[0] is "-h" or "--help")
                ? ExitOk
                : ExitUsage;
        }

        Console.WriteLine(options.Command switch
        {
            CliCommand.Endpoints => "FlowLens - Phase 2 (endpoint discovery)",
            CliCommand.Trace when options.TracesGraphFile => "FlowLens - Phase 3 (graph traversal)",
            CliCommand.Trace => "FlowLens - Phase 2 (call chain)",
            CliCommand.Build => "FlowLens - Phase 3 (graph build)",
            _ => "FlowLens - Phase 1 (Roslyn warm-up)",
        });
        Console.WriteLine();

        // Traversing a built graph needs no Roslyn and no MSBuild: it is a few dictionary lookups
        // over a file. Loading the solution first would add ~16s to answer a question the graph
        // already contains, which is the whole reason graph.json exists.
        if (options.TracesGraphFile)
        {
            return Phase3Commands.Trace(options);
        }

        using var loadResult = await LoadAsync(options.SolutionPath);
        ReportDiagnostics(loadResult);

        var solutionDirectory = Path.GetDirectoryName(options.SolutionPath)!;

        if (options.Command != CliCommand.Scan)
        {
            // A load problem invalidates everything downstream: a silently skipped project makes
            // "nothing calls this" indistinguishable from "we never looked".
            if (loadResult.HasFailures || !loadResult.AllProjectsLoaded)
            {
                return DetermineExitCode(loadResult, compilationFailed: false);
            }

            var exit = options.Command == CliCommand.Build
                ? await Phase3Commands.BuildAsync(loadResult, solutionDirectory, options)
                : await Phase2Commands.RunAsync(loadResult, solutionDirectory, options);

            Console.WriteLine($"Result: exit {exit}");
            return exit;
        }

        var scan = await ScanAsync(loadResult.Solution, solutionDirectory);
        var report = ScanReport.Build(scan);
        ReportScan(report, options);

        if (options.ListMethods)
        {
            ListMethods(scan, options.IncludeTests);
        }

        await RunSemanticDemoAsync(loadResult.Solution, options.DemoTarget, solutionDirectory);

        var compilationFailed = false;
        if (options.CheckCompilation)
        {
            compilationFailed = await RunCompilationCheckAsync(loadResult.Solution);
        }

        return DetermineExitCode(loadResult, compilationFailed);
    }

    // ---------------------------------------------------------------- load

    private static async Task<SolutionLoadResult> LoadAsync(string solutionPath)
    {
        Console.WriteLine($"[1/4] Loading solution");
        Console.WriteLine($"      {solutionPath}");

        var resolved = 0;
        var progress = new Progress<ProjectLoadProgress>(p =>
        {
            // Each project reports Evaluate -> Build -> Resolve; only the last one means
            // "this project is now usable", so reporting the others would triple the noise.
            if (p.Operation != ProjectLoadOperation.Resolve)
            {
                return;
            }

            resolved++;
            Console.WriteLine(
                $"      [{resolved,3}] {Path.GetFileNameWithoutExtension(p.FilePath)} ({p.ElapsedTime.TotalSeconds:F1}s)");
        });

        var result = await SolutionLoader.LoadAsync(solutionPath, progress);

        Console.WriteLine();
        Console.WriteLine(
            $"      Loaded {result.LoadedProjectCount}/{result.ExpectedProjectCount} projects " +
            $"in {result.Elapsed.TotalSeconds:F1}s");

        if (!result.AllProjectsLoaded)
        {
            var missing = result.ExpectedProjectCount - result.LoadedProjectCount;
            Console.WriteLine(
                $"      !! {missing} project(s) in the solution file did not load. " +
                "OpenSolutionAsync skips these silently - it does not throw.");
        }

        Console.WriteLine();
        return result;
    }

    private static void ReportDiagnostics(SolutionLoadResult result)
    {
        Console.WriteLine("[2/4] Workspace diagnostics");

        if (result.Diagnostics.Count == 0)
        {
            Console.WriteLine("      Clean - no failures, no warnings.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine(
            $"      {result.Failures.Count} distinct failure(s), " +
            $"{result.Warnings.Count} distinct warning(s) (deduplicated by message)");
        Console.WriteLine();

        foreach (var diagnostic in result.Diagnostics)
        {
            var repeat = diagnostic.OccurrenceCount > 1 ? $" x{diagnostic.OccurrenceCount}" : string.Empty;
            Console.WriteLine($"      [{diagnostic.Kind}{repeat}] {Truncate(diagnostic.Message, 240)}");
        }

        if (result.HasFailures)
        {
            Console.WriteLine();
            Console.WriteLine(
                "      Note: 'package not found' style failures usually mean the target repo " +
                "has not been restored. Run 'dotnet restore' there and retry.");
        }

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- scan

    private static async Task<ScanResult> ScanAsync(Solution solution, string solutionDirectory)
    {
        Console.WriteLine("[3/4] Scanning method declarations (syntax only, no compilation)");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var scan = await MethodScanner.ScanAsync(solution, solutionDirectory);
        started.Stop();

        Console.WriteLine(
            $"      {scan.Methods.Count} method declarations in {scan.DocumentCount} documents " +
            $"({started.Elapsed.TotalSeconds:F1}s)");

        if (scan.SkippedProjects.Count > 0)
        {
            Console.WriteLine($"      Skipped {scan.SkippedProjects.Count} non-C# project(s): {string.Join(", ", scan.SkippedProjects)}");
        }

        Console.WriteLine(
            "      Counts MethodDeclarationSyntax only - lambdas, constructors, local functions");
        Console.WriteLine(
            "      and property accessors are NOT included. See docs/known-limitations.md.");
        Console.WriteLine();
        return scan;
    }

    private static void ReportScan(ScanReport report, CliOptions options)
    {
        var rows = options.IncludeTests
            ? Merge(report.Modules, report.TestModules)
            : report.Modules;

        PrintModuleTable(rows, report.LayerColumns);

        if (options.IncludeTests)
        {
            Console.WriteLine($"      Total: {report.TotalMethodCount} methods (test projects included).");
        }
        else
        {
            Console.WriteLine($"      Production total: {report.ProductionMethodCount} methods.");
            Console.WriteLine();
            Console.WriteLine(
                $"      Test projects (excluded above): {report.TestMethodCount} methods across " +
                $"{report.TestModules.Count} module bucket(s) - " +
                $"{string.Join(", ", report.TestModules.Select(m => $"{m.Module} {m.Total}"))}");
            Console.WriteLine("      Pass --include-tests to fold them into the table.");
        }

        Console.WriteLine();
    }

    private static void PrintModuleTable(IReadOnlyList<ModuleBreakdown> rows, IReadOnlyList<string> layers)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine("      (no methods found)");
            return;
        }

        var moduleWidth = Math.Max(6, rows.Max(r => r.Module.Length));
        var columnWidths = layers.Select(l => Math.Max(l.Length, 5)).ToArray();

        var header = "      " + "Module".PadRight(moduleWidth);
        for (var i = 0; i < layers.Count; i++)
        {
            header += "  " + layers[i].PadLeft(columnWidths[i]);
        }

        header += "  " + "Total".PadLeft(6);
        Console.WriteLine(header);
        Console.WriteLine("      " + new string('-', header.Length - 6));

        foreach (var row in rows)
        {
            var line = "      " + row.Module.PadRight(moduleWidth);
            for (var i = 0; i < layers.Count; i++)
            {
                var count = row.PerLayer.GetValueOrDefault(layers[i]);
                line += "  " + (count == 0 ? "." : count.ToString()).PadLeft(columnWidths[i]);
            }

            line += "  " + row.Total.ToString().PadLeft(6);
            Console.WriteLine(line);
        }

        Console.WriteLine();
    }

    private static IReadOnlyList<ModuleBreakdown> Merge(
        IReadOnlyList<ModuleBreakdown> first,
        IReadOnlyList<ModuleBreakdown> second) =>
    [
        .. first.Concat(second)
            .GroupBy(b => b.Module, StringComparer.Ordinal)
            .Select(g => new ModuleBreakdown(
                g.Key,
                g.SelectMany(b => b.PerLayer)
                    .GroupBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.Sum(kv => kv.Value), StringComparer.Ordinal),
                g.Sum(b => b.Total)))
            .OrderBy(b => b.Module, StringComparer.Ordinal)
    ];

    private static void ListMethods(ScanResult scan, bool includeTests)
    {
        var methods = includeTests ? scan.Methods : scan.Methods.Where(m => !m.IsTestProject);

        foreach (var method in methods.OrderBy(m => m.FilePath, StringComparer.Ordinal).ThenBy(m => m.Line))
        {
            Console.WriteLine($"      {method.Location} -> {method.ContainingType}.{method.Name}");
        }

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- semantic demo

    private static async Task RunSemanticDemoAsync(
        Solution solution,
        SemanticDemoTarget target,
        string solutionDirectory)
    {
        Console.WriteLine("[4/4] SyntaxTree vs SemanticModel");
        Console.WriteLine(
            $"      Target: {target.ProjectName} / {target.TypeName}.{target.MethodName}");
        Console.WriteLine("      Building a compilation for this ONE project (not the solution)...");

        var demo = await SemanticModelDemo.RunAsync(solution, target, solutionDirectory);
        if (demo is null)
        {
            Console.WriteLine("      !! No suitable method found - demo skipped.");
            Console.WriteLine();
            return;
        }

        if (!demo.UsedPreferredTarget)
        {
            Console.WriteLine($"      !! {demo.FallbackReason}");
        }

        Console.WriteLine();
        Console.WriteLine($"      {demo.TypeName}.{demo.MethodName}  ({demo.FilePath}:{demo.Line})");
        Console.WriteLine();

        Console.WriteLine("      -- Signature -----------------------------------------------------");
        Console.WriteLine("      SyntaxTree sees the text as typed; it depends on the file's usings");
        Console.WriteLine("      and cannot tell you which type 'Result' actually is.");
        Console.WriteLine($"        return : {demo.Signature.SyntaxReturnType}");
        foreach (var parameter in demo.Signature.SyntaxParameters)
        {
            Console.WriteLine($"        param  : {parameter}");
        }

        Console.WriteLine();
        Console.WriteLine("      SemanticModel resolved every name to a symbol: fully qualified,");
        Console.WriteLine("      unambiguous, and carrying the assembly it came from.");
        Console.WriteLine($"        return : {demo.Signature.SemanticReturnType}");
        foreach (var parameter in demo.Signature.SemanticParameters)
        {
            Console.WriteLine($"        param  : {parameter}");
        }

        Console.WriteLine($"        owner  : {demo.Signature.SemanticContainingType}");
        Console.WriteLine($"        asm    : {demo.Signature.SemanticAssembly}");
        Console.WriteLine();

        Console.WriteLine("      -- Invocations ---------------------------------------------------");
        Console.WriteLine("      Left is the callee text in the tree - two identifiers and a dot.");
        Console.WriteLine("      Right is what it binds to. This binding is what Phase 2 traverses.");
        Console.WriteLine();

        foreach (var invocation in demo.Invocations)
        {
            var marker = invocation.Resolved ? " " : "?";
            Console.WriteLine($"      {marker} line {invocation.Line,4}  syntax   : {invocation.SyntaxView}");
            Console.WriteLine($"                    semantic : {invocation.SemanticView}");

            if (invocation.ContainingAssembly is not null)
            {
                var kind = invocation.IsInterfaceMember ? "  <- INTERFACE member" : string.Empty;
                Console.WriteLine($"                    assembly : {invocation.ContainingAssembly}{kind}");
            }

            Console.WriteLine();
        }

        if (demo.TotalInvocationCount > demo.Invocations.Count)
        {
            Console.WriteLine(
                $"      ... {demo.TotalInvocationCount - demo.Invocations.Count} more invocation(s) not shown.");
            Console.WriteLine();
        }

        Console.WriteLine(
            $"      {demo.ResolvedInvocationCount} of {demo.TotalInvocationCount} invocation(s) resolved cleanly; " +
            $"{demo.InterfaceInvocationCount} bound to an interface member.");
        Console.WriteLine(
            "      Interface bindings are the Phase 2 'interface problem': the symbol names the");
        Console.WriteLine(
            "      contract, not the implementation DI will inject. SymbolFinder closes that gap.");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- optional compilation check

    private static async Task<bool> RunCompilationCheckAsync(Solution solution)
    {
        Console.WriteLine("[+] Compiling every project (--check-compilation)");

        var reported = 0;
        var progress = new Progress<string>(name =>
        {
            reported++;
            Console.WriteLine($"      [{reported,3}] {name}");
        });

        var result = await CompilationChecker.CheckAsync(solution, progress);

        Console.WriteLine();
        Console.WriteLine(
            $"      {result.Projects.Count} project(s) compiled in {result.Elapsed.TotalSeconds:F1}s, " +
            $"{result.TotalErrors} error(s), {result.TotalWarnings} warning(s)");

        foreach (var project in result.Failing)
        {
            Console.WriteLine($"      {project.ProjectName}: {project.ErrorCount} error(s)");
            foreach (var sample in project.SampleErrors)
            {
                Console.WriteLine($"        {Truncate(sample, 200)}");
            }
        }

        if (result.TotalErrors > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                "      A project that loads but does not compile still yields a SemanticModel -");
            Console.WriteLine(
                "      just an incomplete one, where some symbols silently resolve to null.");
        }

        Console.WriteLine();
        return result.TotalErrors > 0;
    }

    // ---------------------------------------------------------------- exit

    private static int DetermineExitCode(SolutionLoadResult result, bool compilationFailed)
    {
        if (result.HasFailures || !result.AllProjectsLoaded)
        {
            Console.WriteLine($"Result: LOAD PROBLEM (exit {ExitLoadProblem})");
            return ExitLoadProblem;
        }

        if (compilationFailed)
        {
            Console.WriteLine($"Result: loaded cleanly, but compilation errors found (exit {ExitCompilationErrors})");
            return ExitCompilationErrors;
        }

        Console.WriteLine($"Result: OK (exit {ExitOk})");
        return ExitOk;
    }

    private static string Truncate(string value, int max)
    {
        var single = value.ReplaceLineEndings(" ");
        return single.Length <= max ? single : single[..max] + "...";
    }
}
