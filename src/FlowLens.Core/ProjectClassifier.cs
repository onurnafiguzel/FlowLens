namespace FlowLens.Core;

/// <summary>Where a project sits in the target repository's layout.</summary>
/// <param name="Module">Owning module, e.g. "Ordering". <see cref="UnknownModule"/> when undecidable.</param>
/// <param name="Layer">DDD layer from the project name suffix, or null when it is not one of the known layers.</param>
/// <param name="IsTest">True for anything under tests/.</param>
public sealed record ProjectClassification(string Module, string? Layer, bool IsTest);

/// <summary>
/// Maps a project file path plus assembly name onto (module, layer, isTest).
/// <para>
/// Pure function, no I/O - the path is treated as a string, the file need not exist. The
/// rules are derived from the ModularCommerce survey (docs/modularcommerce-survey.md §1),
/// not guessed.
/// </para>
/// </summary>
public static class ProjectClassifier
{
    public const string UnknownModule = "(unknown)";
    public const string SharedModule = "Shared";
    public const string HostModule = "Host";

    /// <summary>Layer names FlowLens recognises, ordered for report columns.</summary>
    public static readonly IReadOnlyList<string> KnownLayers =
        ["Api", "Application", "Domain", "Infrastructure", "Contracts"];

    public static ProjectClassification Classify(string projectFilePath, string projectName)
    {
        var path = Normalize(projectFilePath);
        var isTest = ContainsSegment(path, "tests");
        var layer = ExtractLayer(projectName);

        // src/Modules/<Module>/ModularCommerce.<Module>.<Layer>/...
        var module = ExtractSegmentAfter(path, "Modules");
        if (module is not null)
        {
            return new ProjectClassification(module, layer, isTest);
        }

        if (ContainsSegment(path, "Shared"))
        {
            return new ProjectClassification(SharedModule, layer, isTest);
        }

        if (ContainsSegment(path, "Bootstrapper"))
        {
            return new ProjectClassification(HostModule, layer, isTest);
        }

        // Test projects live in a flat tests/ folder, so the path carries no module segment;
        // the name does: ModularCommerce.Ordering.UnitTests -> Ordering.
        if (isTest)
        {
            return new ProjectClassification(ExtractModuleFromTestName(projectName), layer, IsTest: true);
        }

        return new ProjectClassification(UnknownModule, layer, isTest);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static bool ContainsSegment(string normalizedPath, string segment) =>
        normalizedPath.Split('/').Any(s => s.Equals(segment, StringComparison.OrdinalIgnoreCase));

    private static string? ExtractSegmentAfter(string normalizedPath, string anchor)
    {
        var segments = normalizedPath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals(anchor, StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return null;
    }

    private static string? ExtractLayer(string projectName)
    {
        var lastSegment = projectName.Split('.').LastOrDefault();
        return lastSegment is not null && KnownLayers.Contains(lastSegment) ? lastSegment : null;
    }

    /// <summary>
    /// ModularCommerce.Ordering.UnitTests -> Ordering. Two-segment names such as
    /// ModularCommerce.ArchitectureTests or ModularCommerce.TestKit carry no module and stay
    /// unknown rather than being forced into a bucket.
    /// </summary>
    private static string ExtractModuleFromTestName(string projectName)
    {
        var segments = projectName.Split('.');
        return segments.Length >= 3 ? segments[1] : UnknownModule;
    }
}
