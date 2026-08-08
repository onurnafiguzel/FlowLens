using System.Reflection;
using System.Text.Json;

namespace FlowLens.Core.Ef;

/// <param name="PackageId">NuGet package id as it appears in the target's deps.json.</param>
/// <param name="TargetVersion">What the target was compiled against. Null when absent from deps.json.</param>
/// <param name="FlowLensVersion">What FlowLens will actually load. Null when the assembly is missing.</param>
public sealed record VersionComparison(
    string PackageId,
    Version? TargetVersion,
    Version? FlowLensVersion,
    bool IsAcceptable,
    string? Problem);

public sealed record VersionGateResult(IReadOnlyList<VersionComparison> Comparisons)
{
    public bool Passed => Comparisons.All(c => c.IsAcceptable);

    public IEnumerable<VersionComparison> Problems => Comparisons.Where(c => !c.IsAcceptable);
}

/// <summary>
/// Pre-flight check that FlowLens's EF Core is compatible with the one the target was built
/// against, run BEFORE any target assembly is loaded.
/// <para>
/// This has to be explicit because the runtime will not complain. The TPA list matches assemblies
/// by simple name and ignores version entirely, so if FlowLens shipped an older EF Core it would
/// bind silently and then fail somewhere deep inside model building as a MissingMethodException -
/// far from the cause and easy to misread as a bug in the target. Failing loudly here trades a
/// confusing runtime crash for a one-line message naming both versions.
/// </para>
/// <para>
/// Read the HOST's deps.json, not a module's. They disagree: the Ordering module records
/// EntityFrameworkCore.Relational 10.0.4 while the host records 10.0.9, because the host also
/// references EntityFrameworkCore.Design which lifts it. The host is what actually ships, so the
/// host is authoritative.
/// </para>
/// </summary>
public static class EfVersionGate
{
    /// <summary>Packages whose types cross the load-context boundary and therefore must unify.</summary>
    public static readonly IReadOnlyList<string> GatedPackages =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Relational",
        "Npgsql.EntityFrameworkCore.PostgreSQL",
    ];

    /// <summary>
    /// Version of the copy FlowLens will actually bind, or null when it is absent.
    /// <para>
    /// For all gated packages the NuGet id equals the assembly's simple name, so the id can be
    /// loaded directly. This resolves through the TPA - the same path the runtime takes when EF
    /// asks - so it reports what will really be used rather than what the csproj asked for.
    /// </para>
    /// </summary>
    public static Version? LoadedVersion(string packageId)
    {
        try
        {
            return Assembly.Load(new AssemblyName(packageId)).GetName().Version;
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or FileLoadException)
        {
            return null;
        }
    }

    public static VersionGateResult Check(string hostAssemblyPath) =>
        Check(hostAssemblyPath, LoadedVersion);

    public static VersionGateResult Check(
        string hostAssemblyPath,
        Func<string, Version?> flowLensVersionLookup)
    {
        var targetVersions = ReadTargetVersions(hostAssemblyPath);

        var comparisons = GatedPackages
            .Select(package => Compare(
                package,
                targetVersions.GetValueOrDefault(package),
                flowLensVersionLookup(package)))
            .ToList();

        return new VersionGateResult(comparisons);
    }

    /// <summary>
    /// Which package reference a human would actually edit to fix a skew. EF Core and its
    /// Relational package both arrive through the Relational reference, so telling someone to bump
    /// "Microsoft.EntityFrameworkCore" would send them to a line that does not exist.
    /// </summary>
    public static string RemediationPackage(string gatedPackage) =>
        gatedPackage.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            ? "Microsoft.EntityFrameworkCore.Relational"
            : gatedPackage;

    /// <summary>Short reason. EfPreflight turns this into the full operator-facing block.</summary>
    private static VersionComparison Compare(string package, Version? target, Version? flowLens)
    {
        if (target is null)
        {
            // Not a failure: the package may genuinely not be referenced by this target.
            return new VersionComparison(package, null, flowLens, IsAcceptable: true, null);
        }

        // Turkish and ASCII-only, because EfPreflight drops these straight into an
        // operator-facing block and the console encoding is not guaranteed.
        if (flowLens is null)
        {
            return new VersionComparison(package, target, null, false, "FlowLens bu paketi hic yuklemiyor");
        }

        if (flowLens.Major != target.Major)
        {
            return new VersionComparison(package, target, flowLens, false, "major surum farkli");
        }

        if (flowLens < target)
        {
            return new VersionComparison(package, target, flowLens, false, "FlowLens hedeften eski");
        }

        return new VersionComparison(package, target, flowLens, IsAcceptable: true, null);
    }

    /// <summary>Where the target's versions were read from, so the message can cite it.</summary>
    public static string DepsJsonPath(string hostAssemblyPath) =>
        Path.ChangeExtension(hostAssemblyPath, ".deps.json");

    /// <summary>
    /// deps.json keys libraries as "Package/Version". Only the version matters here, and only for
    /// the gated packages.
    /// </summary>
    internal static IReadOnlyDictionary<string, Version> ReadTargetVersions(string hostAssemblyPath)
    {
        var depsPath = Path.ChangeExtension(hostAssemblyPath, ".deps.json");
        var versions = new Dictionary<string, Version>(StringComparer.Ordinal);

        if (!File.Exists(depsPath))
        {
            return versions;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));

        if (!document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return versions;
        }

        foreach (var library in libraries.EnumerateObject())
        {
            var separator = library.Name.LastIndexOf('/');
            if (separator <= 0)
            {
                continue;
            }

            var id = library.Name[..separator];
            if (!GatedPackages.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Version.TryParse(library.Name[(separator + 1)..], out var version))
            {
                versions[id] = version;
            }
        }

        return versions;
    }
}
