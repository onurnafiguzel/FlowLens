using Microsoft.CodeAnalysis;

namespace FlowLens.Core.Ef;

/// <param name="HostAssemblyPath">
/// The application's compiled output. Null when the solution has no application project or it has
/// not been built.
/// </param>
/// <param name="Problem">Set when <see cref="HostAssemblyPath"/> cannot be used; already phrased for the user.</param>
public sealed record TargetBuildOutput(
    string? HostAssemblyPath,
    string? HostProjectName,
    bool IsStale,
    string? Problem)
{
    public bool IsUsable => HostAssemblyPath is not null && Problem is null;
}

/// <summary>
/// The Roslyn half of EF model extraction: which DbContext types exist, and where the compiled
/// output that can instantiate them lives.
/// <para>
/// Roslyn is the authoritative list. Reflection can only find what actually loaded, so comparing
/// the two catches a stale build or a dropped assembly instead of quietly producing a graph with a
/// module's tables missing - the Phase 1 "68 vs 66 projects" discipline applied to contexts.
/// </para>
/// </summary>
public static class DbContextDiscovery
{
    public const string DbContextMetadataName = "Microsoft.EntityFrameworkCore.DbContext";

    /// <summary>
    /// Locates the application project's output directory, which is the only place with a complete
    /// dependency closure.
    /// <para>
    /// A class library's bin holds project references only - <c>dotnet build</c> does not copy NuGet
    /// assets there - so a module's own output contains no EF Core at all and cannot anchor the
    /// load context. The application project is the one place where everything has been gathered.
    /// </para>
    /// </summary>
    public static TargetBuildOutput FindBuildOutput(Solution solution, string solutionDirectory)
    {
        var candidates = solution.Projects
            .Where(p => p.CompilationOptions?.OutputKind
                is OutputKind.ConsoleApplication or OutputKind.WindowsApplication)
            .Where(p => !ProjectClassifier.Classify(p.FilePath ?? string.Empty, p.Name).IsTest)
            .ToList();

        if (candidates.Count == 0)
        {
            return new TargetBuildOutput(null, null, false,
                "no application project found in the solution, so there is no output directory " +
                "with a complete dependency closure to load DbContext types from.");
        }

        // More than one application project is possible; prefer the one ProjectClassifier calls
        // the host, since that is the composition root that references every module.
        var host = candidates.FirstOrDefault(p =>
                       ProjectClassifier.Classify(p.FilePath ?? string.Empty, p.Name).Module
                       == ProjectClassifier.HostModule)
                   ?? candidates[0];

        var outputPath = host.OutputFilePath;

        if (string.IsNullOrEmpty(outputPath))
        {
            return new TargetBuildOutput(null, host.Name, false,
                $"{host.Name} reports no output path; the project may not have been evaluated.");
        }

        if (!File.Exists(outputPath))
        {
            return new TargetBuildOutput(null, host.Name, false,
                $"{host.Name} has not been built - {outputPath} does not exist. " +
                "Phase 3 reads EF Core metadata from compiled assemblies, so the target must be " +
                "built first: dotnet build \"" + (solution.FilePath ?? solutionDirectory) + "\"");
        }

        return new TargetBuildOutput(
            outputPath,
            host.Name,
            IsStale: IsStale(outputPath, solutionDirectory),
            Problem: null);
    }

    /// <summary>
    /// True when any source file is newer than the compiled host. A stale build silently answers
    /// yesterday's question, which is worse than refusing.
    /// </summary>
    private static bool IsStale(string outputPath, string solutionDirectory)
    {
        var builtAt = File.GetLastWriteTimeUtc(outputPath);
        var sourceRoot = Path.Combine(solutionDirectory, "src");

        if (!Directory.Exists(sourceRoot))
        {
            return false;
        }

        try
        {
            return Directory
                .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                // obj/ and bin/ hold generated files - AssemblyInfo.cs and friends - which are
                // build OUTPUT. Counting them makes every build look stale the moment any project
                // is rebuilt, which trains the reader to ignore the warning.
                .Where(file => !IsGenerated(file))
                .Any(file => File.GetLastWriteTimeUtc(file) > builtAt);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsGenerated(string path)
    {
        var normalized = path.Replace('\\', '/');

        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every non-abstract type deriving from DbContext in non-test projects, with the source
    /// location of its declaration.
    /// </summary>
    public static async Task<IReadOnlyList<DbContextDeclaration>> FindContextsAsync(
        Solution solution,
        string solutionDirectory,
        CancellationToken cancellationToken = default)
    {
        var found = new List<DbContextDeclaration>();

        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ProjectClassifier.Classify(project.FilePath ?? string.Empty, project.Name).IsTest)
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken);
            var dbContextType = compilation?.GetTypeByMetadataName(DbContextMetadataName);

            if (compilation is null || dbContextType is null)
            {
                // No EF reference in this project: nothing to find, not a problem.
                continue;
            }

            foreach (var type in EnumerateTypes(compilation.Assembly.GlobalNamespace, cancellationToken))
            {
                if (type.IsAbstract || type.TypeKind != TypeKind.Class || !DerivesFrom(type, dbContextType))
                {
                    continue;
                }

                var (file, line) = SourceLocation.For(type, solutionDirectory);

                found.Add(new DbContextDeclaration(
                    ClrTypeName: type.ToDisplayString(NodeId.TypeFormat),
                    AssemblyName: type.ContainingAssembly?.Name ?? project.AssemblyName,
                    Location: line > 0 ? $"{file}:{line}" : file));
            }
        }

        return found;
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(
        INamespaceSymbol ns,
        CancellationToken cancellationToken)
    {
        foreach (var member in ns.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (member)
            {
                case INamespaceSymbol nested:
                    foreach (var type in EnumerateTypes(nested, cancellationToken))
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
