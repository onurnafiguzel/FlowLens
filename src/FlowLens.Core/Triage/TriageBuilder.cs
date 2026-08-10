using System.Globalization;
using FlowLens.Core.Answers;

namespace FlowLens.Core.Triage;

/// <param name="MaxDepth">Traversal depth, same default as the API and the CLI.</param>
/// <param name="BridgeHops">
/// How many nodes a frame link may bridge over. ONE, and deliberately not configurable upward:
/// measured, 72 of the 73 call edges without a call site are interface-to-implementation, which is
/// exactly the hop a stack trace omits because DI dispatches straight to the implementation.
/// Allowing two would let the checker call a route a call.
/// </param>
public sealed record TriageQuery(int MaxDepth = 20, bool IncludeUtility = false, int BridgeHops = 1)
{
    /// <summary>How far to look for a longer route before reporting MissingEdge. Bounded so the report cannot claim an absurd path.</summary>
    public int SkippedFrameSearchDepth => 4;
}

/// <summary>
/// Builds an incident report from a stack trace.
/// <para>
/// This is Phase 4's backward traversal with a different input, not a new system. Entry points and
/// the data layer come straight out of <see cref="AnswerBuilder"/> - the same code the HTTP API and
/// the documentation generator call - so a triage report and a <c>/backward</c> response can never
/// disagree about who reaches a node.
/// </para>
/// </summary>
public static class TriageBuilder
{
    public static TriageReport Build(
        GraphSnapshot snapshot,
        string graphPath,
        string stackTrace,
        string? repoPath,
        TriageQuery? query = null)
    {
        var settings = query ?? new TriageQuery();
        var graph = snapshot.Graph;

        var parsed = StackTraceParser.Parse(stackTrace);
        var matcher = new FrameMatcher(graph);
        var frames = parsed.Frames.Select(matcher.Match).ToList();

        var errorPoint = frames.FirstOrDefault(f => f.Verdict == FrameVerdict.Matched);
        var links = Links(graph, frames, settings);

        var repo = RepoLocator.Locate(repoPath, frames);

        var traversal = new TraversalQuery(settings.MaxDepth, settings.IncludeUtility);

        EntryPointsAnswer? entryPoints = null;
        DataLayerAnswer? downstream = null;
        var limitations = new List<Limitation>();

        if (errorPoint?.Node is { } node)
        {
            var backward = AnswerBuilder.Build(
                graph,
                graph.BackwardSubgraph(node.Id, traversal),
                node.Id,
                TraversalDirection.Backward,
                traversal,
                snapshot.Diagnostics);

            var forward = AnswerBuilder.Build(
                graph,
                graph.ForwardSubgraph(node.Id, traversal),
                node.Id,
                TraversalDirection.Forward,
                traversal,
                snapshot.Diagnostics);

            entryPoints = backward.EntryPoints;
            downstream = forward.DataLayer;
            limitations = Merge(backward.Limitations, forward.Limitations);
        }

        var files = Files(errorPoint, entryPoints);
        var git = GitLog.Read(repo, files);

        return new TriageReport(
            parsed.ExceptionType,
            parsed.Message,
            Counts(parsed, frames),
            graphPath,
            graph.Nodes.Count,
            graph.Edges.Count,
            repo,
            errorPoint?.Node is { } found ? Ref(found) : null,
            Missing(frames),
            frames,
            [.. parsed.Unparsed.Select(l => l.Text.Trim())],
            links,
            entryPoints,
            downstream,
            limitations,
            Diagnostics(snapshot.Diagnostics, errorPoint),
            new CommitSection(git, files));
    }

    /// <summary>
    /// Why there is no error point. A bare "not found" leaves the reader unable to tell a parser
    /// failure from a graph gap, and those need different fixes.
    /// </summary>
    private static string Missing(IReadOnlyList<MatchedFrame> frames)
    {
        if (frames.Any(f => f.Verdict == FrameVerdict.Matched))
        {
            return string.Empty;
        }

        if (frames.Count == 0)
        {
            return "Yigin izinde hic cerceve ayristirilamadi.";
        }

        var project = frames.Count(f => f.Verdict is FrameVerdict.NotInGraph or FrameVerdict.Ambiguous);

        return project == 0
            ? "Cercevelerin hicbiri bir proje namespace'inde degil; hepsi framework ya da ucuncu parti."
            : $"{project} proje cercevesi var ama hicbiri graph'ta bir dugume karsilik gelmiyor.";
    }

    private static TraceCounts Counts(ParsedTrace parsed, IReadOnlyList<MatchedFrame> frames)
    {
        var foreign = frames.Where(f => f.Verdict == FrameVerdict.Foreign).ToList();

        return new TraceCounts(
            parsed.Frames.Count,
            foreign.Count,
            parsed.Count(LineKind.Separator),
            parsed.Count(LineKind.Text),
            parsed.Count(LineKind.Unparsed))
        {
            // The runtime repeats frames - NpgsqlDataReader.NextResult appears twice in a row in a
            // real capture - so "how many foreign frames" and "how many foreign methods" are two
            // different numbers and the report carries both.
            DistinctForeign = foreign
                .Select(f => f.Frame.Key)
                .Distinct(StringComparer.Ordinal)
                .Count(),
        };
    }

    private static List<Limitation> Merge(params IReadOnlyList<Limitation>[] sources) =>
    [
        .. sources
            .SelectMany(s => s)
            .GroupBy(l => l.Code, StringComparer.Ordinal)
            .Select(g => new Limitation(
                g.Key,
                g.First().Message,
                [.. g.SelectMany(l => l.Locations).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))
            .OrderBy(l => l.Code, StringComparer.Ordinal),
    ];

    /// <summary>
    /// Build diagnostics naming the error point's file, with the stronger claim marked separately.
    /// A file match says "this flow touches a raw-SQL region"; a LINE match says "the exception was
    /// thrown on the very line the diagnostic points at". Both are useful and they are not the same
    /// claim, so the report never blurs them.
    /// </summary>
    private static IReadOnlyList<DiagnosticHit> Diagnostics(
        IReadOnlyList<string> diagnostics,
        MatchedFrame? errorPoint)
    {
        if (errorPoint?.Node is not { } node || node.FilePath.Length == 0)
        {
            return [];
        }

        var hits = new List<DiagnosticHit>();

        foreach (var diagnostic in diagnostics)
        {
            if (!diagnostic.Contains(node.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (file, line) = Site(diagnostic);

            hits.Add(new DiagnosticHit(
                diagnostic,
                file,
                line,
                line > 0 && line == errorPoint.Frame.Line));
        }

        return [.. hits.OrderByDescending(h => h.ExactLine).ThenBy(h => h.Line).ThenBy(h => h.Diagnostic, StringComparer.Ordinal)];
    }

    /// <summary>Pulls "... at src/Some/File.cs:37" off the end of a diagnostic line.</summary>
    private static (string File, int Line) Site(string diagnostic)
    {
        var colon = diagnostic.LastIndexOf(':');

        if (colon < 0
            || !int.TryParse(diagnostic[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var line))
        {
            return (string.Empty, 0);
        }

        var text = diagnostic[..colon];
        var space = text.LastIndexOf(' ');

        return (space < 0 ? text : text[(space + 1)..], line);
    }

    /// <summary>
    /// Whose history to read: the error point's file plus every entry point's. Measured on the
    /// captured traces this is 2-5 files and at most 9 commit lines on this target, so no cap is
    /// imposed - a cap would be a number taken from a repository we do not have. The report prints
    /// both counts instead, so an unbounded case becomes visible rather than silently enormous.
    /// </summary>
    private static IReadOnlyList<string> Files(MatchedFrame? errorPoint, EntryPointsAnswer? entryPoints)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);

        if (errorPoint?.Node is { FilePath.Length: > 0 } node)
        {
            files.Add(node.FilePath);
        }

        foreach (var group in entryPoints?.Groups ?? [])
        {
            foreach (var root in group.Nodes.Where(n => n.FilePath.Length > 0))
            {
                files.Add(root.FilePath);
            }
        }

        return [.. files];
    }

    private static IReadOnlyList<FrameLink> Links(
        CodeGraph graph,
        IReadOnlyList<MatchedFrame> frames,
        TriageQuery query)
    {
        var links = new List<FrameLink>();

        // Frames run innermost-first, so the CALLER of frame i is frame i + 1.
        for (var i = 0; i + 1 < frames.Count; i++)
        {
            var callee = frames[i];
            var caller = frames[i + 1];

            links.Add(Link(graph, caller, callee, i + 1, i, query));
        }

        return links;
    }

    private static FrameLink Link(
        CodeGraph graph,
        MatchedFrame caller,
        MatchedFrame callee,
        int callerIndex,
        int calleeIndex,
        TriageQuery query)
    {
        if (caller.Node is not { } from || callee.Node is not { } to)
        {
            return new FrameLink(callerIndex, calleeIndex, LinkVerdict.NotChecked, string.Empty, [], []);
        }

        if (string.Equals(from.Id, to.Id, StringComparison.Ordinal))
        {
            return new FrameLink(callerIndex, calleeIndex, LinkVerdict.SameMethod, string.Empty, [], []);
        }

        var outgoing = graph.Edges
            .Where(e => e.Kind == EdgeKind.Calls && string.Equals(e.FromId, from.Id, StringComparison.Ordinal))
            .ToList();

        var line = caller.Frame.Line;
        var known = new SortedSet<int>();

        // Direct edge first.
        foreach (var edge in outgoing.Where(e => string.Equals(e.ToId, to.Id, StringComparison.Ordinal)))
        {
            foreach (var site in edge.CallSites)
            {
                known.Add(site.Line);
            }
        }

        var direct = outgoing.Any(e => string.Equals(e.ToId, to.Id, StringComparison.Ordinal));

        if (direct && known.Contains(line))
        {
            return new FrameLink(callerIndex, calleeIndex, LinkVerdict.Verified, string.Empty, [], []);
        }

        // One bridged hop. The middle edge must carry NO call site: that is what a
        // interface-to-implementation edge looks like in this graph (measured 72 of 73), and it is
        // a property of the data rather than a guess from the type's name.
        if (query.BridgeHops >= 1)
        {
            foreach (var first in outgoing.OrderBy(e => e.ToId, StringComparer.Ordinal))
            {
                var bridged = graph.Edges.Any(e =>
                    e.Kind == EdgeKind.Calls
                    && e.CallSites.Count == 0
                    && string.Equals(e.FromId, first.ToId, StringComparison.Ordinal)
                    && string.Equals(e.ToId, to.Id, StringComparison.Ordinal));

                if (!bridged)
                {
                    continue;
                }

                foreach (var site in first.CallSites)
                {
                    known.Add(site.Line);
                }

                if (first.CallSites.Any(s => s.Line == line))
                {
                    return new FrameLink(callerIndex, calleeIndex, LinkVerdict.Verified, first.ToId, [], []);
                }
            }
        }

        if (known.Count > 0)
        {
            return new FrameLink(callerIndex, calleeIndex, LinkVerdict.LineMismatch, string.Empty, [.. known], []);
        }

        var path = Route(graph, from.Id, to.Id, query.SkippedFrameSearchDepth);

        return path.Count > 0
            ? new FrameLink(callerIndex, calleeIndex, LinkVerdict.SkippedFrames, string.Empty, [], path)
            : new FrameLink(callerIndex, calleeIndex, LinkVerdict.MissingEdge, string.Empty, [], []);
    }

    /// <summary>
    /// Shortest CALLS route between two nodes, up to a bounded depth. Used only to distinguish
    /// "the graph knows a longer way round" from "the graph knows nothing" - it is an observation
    /// about the graph, never a claim that the missing frames were inlined.
    /// </summary>
    private static IReadOnlyList<string> Route(CodeGraph graph, string fromId, string toId, int maxDepth)
    {
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { fromId };
        var frontier = new List<string> { fromId };

        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();

            foreach (var current in frontier)
            {
                var targets = graph.Edges
                    .Where(e => e.Kind == EdgeKind.Calls && string.Equals(e.FromId, current, StringComparison.Ordinal))
                    .Select(e => e.ToId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal);

                foreach (var target in targets)
                {
                    if (!seen.Add(target))
                    {
                        continue;
                    }

                    previous[target] = current;

                    if (string.Equals(target, toId, StringComparison.Ordinal))
                    {
                        return Unwind(previous, fromId, toId);
                    }

                    next.Add(target);
                }
            }

            frontier = next;
        }

        return [];
    }

    private static IReadOnlyList<string> Unwind(Dictionary<string, string> previous, string fromId, string toId)
    {
        var path = new List<string>();

        for (var current = toId; !string.Equals(current, fromId, StringComparison.Ordinal); current = previous[current])
        {
            path.Add(current);
        }

        path.Reverse();
        return path;
    }

    private static NodeRef Ref(Node node) =>
        new(node.Id, node.Kind, node.RootKind, node.DisplayName, node.Module, node.FilePath,
            node.Line, 0, node.Ambiguous, node.Utility);
}
