using System.Text.Json;
using FlowLens.Core;
using FlowLens.Core.Triage;

namespace FlowLens.Cli;

/// <summary>
/// Turns a stack trace into an incident report.
/// <para>
/// No solution load, no LLM, no automation: the graph is read from graph.json and git is only ever
/// read from. Phase 4's backward traversal with a different input, which is the whole point of the
/// phase - the answer already existed, only the way of asking for it was missing.
/// </para>
/// </summary>
public static class TriageCommand
{
    public static int Run(CliOptions options)
    {
        var resolution = GraphPathResolver.Resolve(options.GraphPath);
        var source = new GraphSource(resolution);
        var snapshot = source.Refresh();

        if (snapshot is null)
        {
            Console.Error.WriteLine($"error: {source.LoadError}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("      Denenen yollar:");

            foreach (var attempt in source.AttemptedPaths)
            {
                Console.Error.WriteLine($"        {attempt}");
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine("      Cozum: flowlens build <solution-path> -o graph.json");
            return Runner.ExitNotFound;
        }

        var text = ReadStackTrace(options, snapshot, out var readError);

        if (text is null)
        {
            Console.Error.WriteLine($"error: {readError}");
            return Runner.ExitUsage;
        }

        var report = TriageBuilder.Build(snapshot, source.Path, text, options.RepoPath);

        var rendered = options.JsonOutput
            ? JsonSerializer.Serialize(report, JsonOptions)
            : TriageMarkdown.Render(report);

        if (options.OutputDirectory is { Length: > 0 } path)
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
            File.WriteAllText(full, rendered.ReplaceLineEndings("\n"));
            Console.WriteLine($"      rapor : {full}");
        }
        else
        {
            Console.WriteLine();
            Console.Write(rendered);
            Console.WriteLine();
        }

        if (report.Unresolved)
        {
            Console.Error.WriteLine($"error: {report.ErrorPointMissing}");
            return Runner.ExitNotFound;
        }

        // The graph half is complete and correct; only git is missing. Reporting success would
        // hide that, and refusing to print anything would throw away the part that IS right.
        return report.Incomplete ? Runner.ExitIncomplete : Runner.ExitOk;
    }

    /// <summary>Same shape as graph.json and the HTTP API: camelCase, indented, enums as names.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// The two documented inputs: a stack trace, or an exception type plus a method name. The
    /// second goes through <see cref="NodeResolver"/>, the same resolver the CLI and the HTTP API
    /// already use, so there is no second way of naming a node.
    /// </summary>
    private static string? ReadStackTrace(CliOptions options, GraphSnapshot snapshot, out string? error)
    {
        error = null;

        if (options.MethodSelector is { Length: > 0 } selector)
        {
            var id = NodeResolver.Resolve(snapshot.Graph, selector);

            if (id is null)
            {
                var near = NodeResolver.NearMatches(snapshot.Graph, selector);

                error = $"\"{selector}\" hicbir node ile eslesmiyor."
                    + (near.Count == 0 ? string.Empty : $" Yakin adaylar: {string.Join(" | ", near)}");

                return null;
            }

            var node = snapshot.Graph.Find(id)!;
            var exception = options.ExceptionType ?? "System.Exception";

            // A one-frame trace built from the node's own recorded position. Honest: every field
            // comes from the graph, nothing about a call site is invented.
            return $"{exception}: (yigin izi verilmedi; --method ile secildi)\n"
                + $"   at {Signature(node.Id)} in {node.FilePath}:line {node.Line}";
        }

        if (options.StackTracePath is not { Length: > 0 } path)
        {
            error = "triage bir yigin izi ister: --stack-trace <dosya|-> ya da --method \"<Tip.Metot>\".";
            return null;
        }

        if (path == "-")
        {
            return Console.In.ReadToEnd();
        }

        var full = Path.GetFullPath(path);

        if (!File.Exists(full))
        {
            error = $"yigin izi dosyasi yok: {full}";
            return null;
        }

        return File.ReadAllText(full);
    }

    /// <summary>Renders a node id the way the runtime would render the frame, so the parser reads it back unchanged.</summary>
    private static string Signature(string nodeId) =>
        nodeId.Contains('(', StringComparison.Ordinal) ? nodeId : nodeId + "()";
}
