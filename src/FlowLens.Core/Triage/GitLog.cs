using System.Diagnostics;

namespace FlowLens.Core.Triage;

public sealed record Commit(string Sha, string Subject);

/// <param name="Error">Null on success. Set means the report is produced WITHOUT this file's history.</param>
public sealed record FileHistory(string FilePath, IReadOnlyList<Commit> Commits, string? Error);

/// <param name="Head">Short sha of HEAD. What makes the git half of a report reproducible.</param>
/// <param name="Files">One entry per file, in ordinal order. Errors are per file, not fatal.</param>
public sealed record GitAnswer(
    bool Available,
    string? Error,
    string Head,
    IReadOnlyList<FileHistory> Files)
{
    public int CommitLines => Files.Sum(f => f.Commits.Count);
}

/// <summary>
/// Reads recent history with the git CLI.
/// <para>
/// <b>Read-only by construction, not by intention.</b> The two subcommands this type can issue are
/// fixed constants; there is no code path that builds an arbitrary git invocation, so "no git write
/// operations" is a property of the callable surface rather than a rule someone has to remember.
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so a path containing a space
/// or a quote cannot change what runs.
/// </para>
/// <para>
/// LibGit2Sharp was rejected: a new NuGet dependency needs approval under roadmap rule 3, and the
/// roadmap already specifies <c>git log --oneline -5</c>. A library for one read is more surface,
/// not less.
/// </para>
/// </summary>
public static class GitLog
{
    /// <summary>The roadmap's number. Measured on this target: no file reaches it (23 commits total, 1-3 per file).</summary>
    public const int DefaultCount = 5;

    public static GitAnswer Read(RepoLocation repo, IReadOnlyList<string> files, int count = DefaultCount)
    {
        if (!repo.Found)
        {
            return new GitAnswer(false, repo.Error ?? "Repo koku bulunamadi.", string.Empty, []);
        }

        var head = Run(repo.Root, ["rev-parse", "--short", "HEAD"]);

        if (head.Error is not null)
        {
            return new GitAnswer(false, head.Error, string.Empty, []);
        }

        var histories = new List<FileHistory>();

        foreach (var file in files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var log = Run(repo.Root, ["log", $"--max-count={count}", "--oneline", "--no-decorate", "--", file]);

            histories.Add(log.Error is not null
                ? new FileHistory(file, [], log.Error)
                : new FileHistory(file, Commits(log.Output), null));
        }

        return new GitAnswer(true, null, head.Output.Trim(), histories);
    }

    private static IReadOnlyList<Commit> Commits(string output) =>
        [
            .. output
                .ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var space = line.IndexOf(' ');

                    return space < 0
                        ? new Commit(line.Trim(), string.Empty)
                        : new Commit(line[..space], line[(space + 1)..].Trim());
                }),
        ];

    private static (string Output, string? Error) Run(string workingDirectory, string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);

            if (process is null)
            {
                return (string.Empty, "git baslatilamadi.");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                process.Kill(entireProcessTree: true);
                return (string.Empty, $"git {arguments[0]} 20 saniyede bitmedi.");
            }

            return process.ExitCode == 0
                ? (output, null)
                : (string.Empty,
                    $"git {string.Join(' ', arguments)} exit {process.ExitCode}: {Trim(error)}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (string.Empty, $"git calistirilamadi ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private static string Trim(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
        return line?.Trim() ?? "(stderr bos)";
    }
}
