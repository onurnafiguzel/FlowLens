namespace FlowLens.Core.Triage;

/// <summary>How the target repository was located. Printed in the report, never inferred by the reader.</summary>
public enum RepoOrigin
{
    /// <summary>Named on the command line. Never silently replaced by anything else.</summary>
    Given,

    /// <summary>Computed from a frame's absolute path minus the node's repo-relative path.</summary>
    DerivedFromStackTrace,

    /// <summary>Not located. The report is produced anyway, without commits.</summary>
    NotFound,
}

/// <param name="Attempts">Every root that was tried and why it was rejected. A single rejected path says where we looked, not why there.</param>
public sealed record RepoLocation(
    RepoOrigin Origin,
    string Root,
    string Evidence,
    string? Error,
    IReadOnlyList<string> Attempts)
{
    public bool Found => Origin != RepoOrigin.NotFound;
}

/// <summary>
/// Finds the target repository root.
/// <para>
/// It is not in graph.json: the <c>solution</c> field holds <c>"ModularCommerce.sln"</c>, a file
/// name with no path. But a real stack trace already carries absolute paths from the PDB, and
/// every graph node carries the matching repo-relative path, so the root is the difference between
/// them - derived from data already in the input rather than asked for again.
/// </para>
/// <para>
/// <b>An explicitly named root is never replaced.</b> Same rule as
/// <see cref="GraphPathResolver"/>'s configured path: if the operator names a repository and it is
/// wrong, silently reading a different one turns a wrong answer into a confident wrong answer.
/// </para>
/// </summary>
public static class RepoLocator
{
    public static RepoLocation Locate(string? given, IReadOnlyList<MatchedFrame> frames)
    {
        var attempts = new List<string>();

        if (!string.IsNullOrWhiteSpace(given))
        {
            var root = Path.GetFullPath(given);
            attempts.Add(root);

            return Directory.Exists(root)
                ? new RepoLocation(RepoOrigin.Given, root, "--repo ile verildi", null, attempts)
                : new RepoLocation(
                    RepoOrigin.NotFound,
                    string.Empty,
                    string.Empty,
                    $"--repo ile verilen dizin yok: {root}. Verilmis bir yol asla baskasiyla degistirilmez.",
                    attempts);
        }

        foreach (var candidate in Candidates(frames))
        {
            attempts.Add(candidate.Root);

            if (Directory.Exists(candidate.Root))
            {
                return new RepoLocation(
                    RepoOrigin.DerivedFromStackTrace,
                    candidate.Root,
                    candidate.Evidence,
                    null,
                    attempts);
            }
        }

        return new RepoLocation(
            RepoOrigin.NotFound,
            string.Empty,
            string.Empty,
            attempts.Count == 0
                ? "Hicbir cerceve hem mutlak yol hem graph dugumu tasimiyor; repo koku turetilemedi. --repo verin."
                : "Turetilen koklerin hicbiri bir dizin degil. --repo verin.",
            attempts);
    }

    /// <summary>
    /// Roots implied by frames whose absolute path ends with their node's repo-relative path.
    /// Ordered by root so two runs over the same trace agree even when several frames imply
    /// different roots - the discovery, not just the output, has to be deterministic.
    /// </summary>
    private static IEnumerable<(string Root, string Evidence)> Candidates(IReadOnlyList<MatchedFrame> frames)
    {
        var found = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in frames)
        {
            if (match.Node is not { } node
                || !match.Frame.HasLocation
                || node.FilePath.Length == 0)
            {
                continue;
            }

            var absolute = Slashes(match.Frame.FilePath);
            var relative = Slashes(node.FilePath);

            if (relative.Length == 0 || !absolute.EndsWith(relative, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var root = absolute[..^relative.Length].TrimEnd('/');

            if (root.Length > 0)
            {
                found.TryAdd(
                    Path.GetFullPath(root),
                    $"{match.Frame.FilePath} eksi {node.FilePath}");
            }
        }

        return found.Select(entry => (entry.Key, entry.Value));
    }

    private static string Slashes(string path) => path.Replace('\\', '/');
}
