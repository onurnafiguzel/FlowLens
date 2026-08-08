using System.Runtime.CompilerServices;

namespace FlowLens.Tests;

/// <summary>
/// Enforces the boundary that makes the out-of-process escape hatch cheap.
/// <para>
/// EfProbe is the only class allowed to reference EF Core or Npgsql. That is not a style rule: it
/// is the reason moving the model read into a separate process would touch one file instead of the
/// whole data layer (docs/known-limitations.md, L14). A boundary maintained by discipline erodes on
/// the first convenient exception, so it is checked by the compiler's own inputs instead.
/// </para>
/// </summary>
public sealed class EfProbeArchitectureTests
{
    private const string AllowedFile = "EfProbe.cs";

    [Fact]
    public void OnlyEfProbeReferencesEntityFrameworkOrNpgsql()
    {
        var offenders = SourceFiles()
            .Where(file => ReferencesEfTypes(File.ReadAllLines(file)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal([AllowedFile], offenders);
    }

    /// <summary>
    /// A guard on the guard: if the scan stops finding files, the assertion above would pass by
    /// finding nothing rather than by the boundary holding.
    /// </summary>
    [Fact]
    public void TheScanActuallySeesTheSourceTree()
    {
        var files = SourceFiles().ToList();

        Assert.Contains(files, f => Path.GetFileName(f) == AllowedFile);
        Assert.Contains(files, f => Path.GetFileName(f) == "GraphBuilder.cs");
    }

    /// <summary>
    /// Using directives, not raw text. Several classes legitimately mention EF type NAMES as
    /// strings - the analyzers compare <c>"Microsoft.EntityFrameworkCore.DbContext"</c> against
    /// Roslyn symbols, and the version gate names packages - and that is precisely the arrangement
    /// this test protects: names travel as data, types do not travel at all.
    /// </summary>
    private static bool ReferencesEfTypes(IEnumerable<string> lines) =>
        lines.Any(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("using Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || trimmed.StartsWith("using Npgsql", StringComparison.Ordinal);
        });

    private static IEnumerable<string> SourceFiles() =>
        Directory
            .EnumerateFiles(CoreSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal))
            .Where(f => !f.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal));

    /// <summary>
    /// Located from this file rather than from the working directory, so the test does not depend
    /// on how the runner was invoked.
    /// </summary>
    private static string CoreSourceDirectory
    {
        get
        {
            var directory = Path.GetDirectoryName(ThisFile())!;

            while (directory is not null && !Directory.Exists(Path.Combine(directory, "src")))
            {
                directory = Path.GetDirectoryName(directory);
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!, "src", "FlowLens.Core");
        }
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
