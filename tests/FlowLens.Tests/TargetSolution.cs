using System.Text.Json;

namespace FlowLens.Tests;

/// <summary>
/// Resolves the target solution the integration tests analyse.
/// <para>
/// Deliberately has no "give up quietly" path. A test that skips itself when its fixture is
/// missing reports green while verifying nothing, which is worse than no test at all: the
/// suite keeps passing after the thing it guards has broken. So a missing or misconfigured
/// path throws with instructions instead.
/// </para>
/// </summary>
public static class TargetSolution
{
    public const string EnvironmentVariable = "FLOWLENS_TARGET_SLN";
    private const string SettingsFileName = "appsettings.test.json";
    private const string SettingsKey = "targetSolutionPath";

    /// <summary>Absolute path to the target .sln. Throws if it cannot be resolved.</summary>
    public static string Path
    {
        get
        {
            var (path, source) = Resolve();

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(BuildNotConfiguredMessage());
            }

            var absolute = System.IO.Path.GetFullPath(path);

            if (!File.Exists(absolute))
            {
                throw new FileNotFoundException(
                    $"""
                     The target solution configured via {source} does not exist.

                       configured : {path}
                       resolved   : {absolute}

                     {BuildNotConfiguredMessage()}
                     """,
                    absolute);
            }

            return absolute;
        }
    }

    public static string Directory => System.IO.Path.GetDirectoryName(Path)!;

    private static (string? Path, string Source) Resolve()
    {
        // Environment variable wins so CI can point at a checkout without editing files.
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return (fromEnvironment, $"the {EnvironmentVariable} environment variable");
        }

        var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            return (null, SettingsFileName);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var configured = document.RootElement.TryGetProperty(SettingsKey, out var element)
            ? element.GetString()
            : null;

        // Relative paths in the settings file are resolved against the file itself, so the
        // default "../ModularCommerce" style entry survives being copied to the output folder.
        if (!string.IsNullOrWhiteSpace(configured) && !System.IO.Path.IsPathRooted(configured))
        {
            configured = System.IO.Path.Combine(AppContext.BaseDirectory, configured);
        }

        return (configured, $"{SettingsFileName} ({SettingsKey})");
    }

    private static string BuildNotConfiguredMessage() =>
        $"""
         FlowLens integration tests need a real C# solution to analyse. Configure one:

           1. Set the {EnvironmentVariable} environment variable to an absolute .sln path, or
           2. Edit tests/FlowLens.Tests/{SettingsFileName} and set "{SettingsKey}".

         The default expects ModularCommerce checked out next to FlowLens:
           <repos>/FlowLens
           <repos>/ModularCommerce/ModularCommerce.sln

         Also make sure the target has been restored ('dotnet restore'), otherwise
         MSBuildWorkspace reports load failures for every project.
         """;
}
