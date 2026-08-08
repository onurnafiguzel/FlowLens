namespace FlowLens.Tests;

/// <summary>
/// Locates the target's compiled output.
/// <para>
/// Phase 3 needs the target BUILT, not merely present - a prerequisite phases 1 and 2 did not have.
/// Like <see cref="TargetSolution"/>, this throws with instructions rather than letting a test skip
/// or fail on a null reference somewhere further along.
/// </para>
/// </summary>
public static class TargetBuildPaths
{
    /// <summary>
    /// The application's output, not a module's: <c>dotnet build</c> does not copy NuGet assets
    /// into a class library's bin, so only the host directory has a complete dependency closure.
    /// </summary>
    public static string HostAssembly
    {
        get
        {
            var path = Path.Combine(
                TargetSolution.Directory,
                "src", "Bootstrapper", "ModularCommerce.Host",
                "bin", "Debug", "net10.0", "ModularCommerce.Host.dll");

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"""
                     Phase 3 reads EF Core metadata from compiled assemblies, so the target must be
                     built before these tests can run.

                       expected : {path}

                     Run:
                       dotnet build "{TargetSolution.Path}"
                     """,
                    path);
            }

            return path;
        }
    }
}
