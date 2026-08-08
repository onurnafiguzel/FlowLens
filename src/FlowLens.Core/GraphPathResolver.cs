namespace FlowLens.Core;

/// <param name="Path">Where the graph was found, or the best guess when it was not.</param>
/// <param name="Found">False when nothing matched - the caller must say so rather than answer emptily.</param>
/// <param name="Attempted">Every path tried, in order. This is what a "not found" message owes the reader.</param>
/// <param name="Diagnostics">
/// The directories the search depended on. Printed at startup because the whole class of confusion
/// here comes from CurrentDirectory and ContentRootPath disagreeing, and neither being visible.
/// </param>
public sealed record GraphPathResolution(
    string Path,
    bool Found,
    IReadOnlyList<string> Attempted,
    IReadOnlyDictionary<string, string> Diagnostics);

/// <summary>
/// Finds graph.json.
/// <para>
/// Needed because <c>dotnet run --project src/FlowLens.Api</c> sets the process working directory
/// to the PROJECT folder, not to where the operator is standing. A relative "graph.json" therefore
/// resolves under src/FlowLens.Api, the file in the repository root is never seen, and every data
/// endpoint answers 503 while the graph sits two directories up. Resolving against the content root
/// has the same problem for the same reason.
/// </para>
/// <para>
/// So the search is explicit and ordered, and it reports what it tried. The upward walk mirrors how
/// the target solution is located elsewhere in this repository: start where the code is and climb
/// until the repository root, rather than depending on how the process happened to be launched.
/// </para>
/// </summary>
public static class GraphPathResolver
{
    public const string DefaultFileName = "graph.json";

    /// <summary>Markers that say "this is the repository root, stop climbing".</summary>
    private static readonly string[] RootMarkers = ["FlowLens.slnx", "FlowLens.sln", ".git"];

    public static GraphPathResolution Resolve(string? configured, string fileName = DefaultFileName)
    {
        var attempted = new List<string>();

        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["currentDirectory"] = Environment.CurrentDirectory,
            ["baseDirectory"] = AppContext.BaseDirectory,
        };

        // (a) An explicit path wins outright, absolute or relative. Relative is resolved against the
        // working directory, which is what someone typing a path on a command line means by it.
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var explicitPath = System.IO.Path.GetFullPath(configured);
            attempted.Add(explicitPath);

            // Returned even when missing: a path the operator named and a path we guessed are not
            // the same kind of failure, and silently falling back to a different graph than the one
            // asked for would be worse than not finding it.
            return new GraphPathResolution(explicitPath, File.Exists(explicitPath), attempted, diagnostics);
        }

        // (b) Where the operator is standing.
        var fromCurrent = System.IO.Path.GetFullPath(fileName);
        attempted.Add(fromCurrent);

        if (File.Exists(fromCurrent))
        {
            return new GraphPathResolution(fromCurrent, true, attempted, diagnostics);
        }

        // (c) Climb from the binary towards the repository root. This is what makes
        // `dotnet run --project ...` work from anywhere: bin/Release/net10.0 is several levels
        // below the root, and the graph lives at the top.
        foreach (var candidate in Climb(AppContext.BaseDirectory, fileName))
        {
            attempted.Add(candidate);

            if (File.Exists(candidate))
            {
                return new GraphPathResolution(candidate, true, attempted, diagnostics);
            }
        }

        return new GraphPathResolution(fromCurrent, false, attempted, diagnostics);
    }

    /// <summary>
    /// Candidate paths from a starting directory up to the repository root inclusive. Stops at the
    /// root marker so the search can never wander into a sibling checkout or a home directory.
    /// </summary>
    private static IEnumerable<string> Climb(string start, string fileName)
    {
        var directory = new DirectoryInfo(start);

        while (directory is not null)
        {
            yield return System.IO.Path.Combine(directory.FullName, fileName);

            if (RootMarkers.Any(marker => Path.Exists(System.IO.Path.Combine(directory.FullName, marker))))
            {
                yield break;
            }

            directory = directory.Parent;
        }
    }
}
