using System.Runtime.CompilerServices;

namespace FlowLens.Tests;

/// <summary>
/// Locates FlowLens's own committed graph.json.
/// <para>
/// The API tests run against the real graph rather than a fixture: a synthetic three-node file
/// would exercise the plumbing and none of the shapes that matter - raw-SQL diagnostics, a
/// background-job root, second-class mechanisms. Reading it costs milliseconds and loads no
/// solution, which is exactly the property the phase is claiming.
/// </para>
/// <para>
/// Missing, it throws with the command that produces it. It never causes a skip: a suite that
/// quietly runs fewer tests reports green for coverage it does not have.
/// </para>
/// </summary>
public static class TestPaths
{
    public static string RepositoryGraph
    {
        get
        {
            var path = Path.Combine(RepositoryRoot, "graph.json");

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"""
                     The API answers from graph.json and nothing else, so these tests need it.

                       expected : {path}

                     Run:
                       dotnet run --project src/FlowLens.Cli -- build <solution-path> -o graph.json
                     """,
                    path);
            }

            return path;
        }
    }

    /// <summary>From this file's own compile-time path, so it survives any output layout.</summary>
    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
