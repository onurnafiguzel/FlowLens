using FlowLens.Core;
using FlowLens.Core.Answers;
using FlowLens.Core.Ef;

namespace FlowLens.Cli;

/// <summary>Phase 3 CLI surface: build the graph, and traverse a built graph.</summary>
public static class Phase3Commands
{
    public static async Task<int> BuildAsync(
        SolutionLoadResult loadResult,
        string solutionDirectory,
        CliOptions options)
    {
        Console.WriteLine("[3/4] Building the graph");
        Console.WriteLine("      roots: every endpoint, every IConsumer, every hosted service");
        Console.WriteLine();

        GraphBuildResult result;

        try
        {
            result = await GraphBuilder.BuildAsync(
                loadResult.Solution,
                solutionDirectory,
                new TraversalOptions(
                    MaxDepth: options.MaxDepth,
                    ImplementationPolicy: options.ImplementationPolicy));
        }
        catch (EfPreflightException ex)
        {
            // No graph is written. A file that looks complete but is missing a module's tables is
            // worse than no file: the next reader has no way to tell. There is deliberately no
            // --allow-missing-model escape - the answer to a target FlowLens cannot read is to
            // change the architecture (known-limitations L14), not to label a broken output.
            Console.Error.WriteLine();
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            return Runner.ExitModelUnavailable;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return Runner.ExitIncomplete;
        }

        ReportBuild(result);

        var path = Path.GetFullPath(options.GraphPath ?? CliOptions.DefaultGraphPath);

        try
        {
            GraphJson.Write(path, result.Document);
        }
        catch (InvalidGraphException ex)
        {
            // The roadmap makes filePath+line mandatory for every node, so an unattributable graph
            // is not written at all - a file nobody can check against the source is worse than none.
            Console.Error.WriteLine();
            Console.Error.WriteLine($"error: {ex.Message}");
            return Runner.ExitInvalidGraph;
        }

        Console.WriteLine($"      Wrote {path}");
        Console.WriteLine();

        // Reaching here means preflight passed, so the model is complete by construction.
        return Runner.ExitOk;
    }

    public static int Trace(CliOptions options)
    {
        var path = Path.GetFullPath(options.GraphPath!);

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"error: {path} does not exist. Run 'flowlens build <solution>' first.");
            return Runner.ExitNotFound;
        }

        GraphDocument document;

        try
        {
            document = GraphJson.Read(path);
        }
        catch (Exception ex) when (ex is InvalidGraphException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"error: {path} could not be read - {ex.Message}");
            return Runner.ExitInvalidGraph;
        }

        var graph = GraphJson.ToGraph(document);
        var startId = NodeResolver.Resolve(graph, options.EndpointSelector!);

        if (startId is null)
        {
            ReportNoMatch(graph, options.EndpointSelector!);
            return Runner.ExitNotFound;
        }

        var query = new TraversalQuery(options.MaxDepth, options.IncludeUtility);

        var subgraph = options.Direction == TraversalDirection.Forward
            ? graph.ForwardSubgraph(startId, query)
            : graph.BackwardSubgraph(startId, query);

        Print(graph, subgraph, startId, options.Direction);
        return Runner.ExitOk;
    }

    // ---------------------------------------------------------------- reporting

    private static void ReportBuild(GraphBuildResult result)
    {
        var stats = result.Document.Stats;

        Console.WriteLine($"      {result.Roots.Count} roots: " + string.Join(", ", result.Roots
            .GroupBy(r => r.Kind)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {Label(g.Key, g.Count())}")));

        Console.WriteLine();
        Console.WriteLine("      Nodes by type:");
        foreach (var (kind, count) in stats.NodesByType.OrderByDescending(kv => kv.Value))
        {
            Console.WriteLine($"        {kind,-14} {count,6}");
        }

        Console.WriteLine();
        Console.WriteLine("      Edges by type:");
        foreach (var (kind, count) in stats.EdgesByType.OrderByDescending(kv => kv.Value))
        {
            Console.WriteLine($"        {kind,-14} {count,6}");
        }

        if (stats.EdgesByMechanism.Count > 0)
        {
            // The point of this table: a WRITES edge inferred from a bare SaveChanges is a weaker
            // claim than one read off context.Orders.Add, and only this breakdown shows the split.
            Console.WriteLine();
            Console.WriteLine("      Edges by mechanism (second-class ones are marked):");
            foreach (var (mechanism, count) in stats.EdgesByMechanism.OrderByDescending(kv => kv.Value))
            {
                var marker = mechanism is nameof(EdgeMechanism.SaveChangesWithEntityParameter)
                    or nameof(EdgeMechanism.EntityConstruction)
                    ? "  <- second class"
                    : string.Empty;

                Console.WriteLine($"        {mechanism,-32} {count,6}{marker}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"      {stats.AmbiguousNodes} ambiguous · {stats.UtilityNodes} utility (Shared) · " +
            $"{stats.ElapsedMs / 1000.0:F1}s");

        ReportModel(result);

        if (result.Diagnostics.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"      Diagnostics ({result.Diagnostics.Count}) - what the graph knowingly omits:");
            foreach (var diagnostic in result.Diagnostics.Take(30))
            {
                Console.WriteLine($"        {diagnostic}");
            }

            if (result.Diagnostics.Count > 30)
            {
                Console.WriteLine($"        ... {result.Diagnostics.Count - 30} more");
            }
        }

        Console.WriteLine();
    }

    private static void ReportModel(GraphBuildResult result)
    {
        var model = result.ModelResult;

        Console.WriteLine();
        Console.WriteLine(
            $"      EF model: {model.Snapshots.Count} context(s), " +
            $"{model.Snapshots.Sum(s => s.Entities.Count)} entity type(s) " +
            $"({model.Elapsed.TotalSeconds:F1}s)");

        if (model.Snapshots.Count == 0)
        {
            Console.WriteLine("      !! No EF model was read - every table and column is missing from the graph.");
            return;
        }

        foreach (var snapshot in model.Snapshots.OrderBy(s => s.ContextClrTypeName, StringComparer.Ordinal))
        {
            var tables = snapshot.Entities
                .Where(e => e.QualifiedTableName is not null)
                .Select(e => e.QualifiedTableName!)
                .Distinct(StringComparer.Ordinal)
                .Count();

            Console.WriteLine(
                $"        {Short(snapshot.ContextClrTypeName),-24} schema {snapshot.DefaultSchema,-14} " +
                $"{snapshot.Entities.Count,3} entities, {tables,3} tables");
        }
    }

    private static void Print(CodeGraph graph, Subgraph subgraph, string startId, TraversalDirection direction)
    {
        var start = graph.Find(startId)!;
        var arrow = direction == TraversalDirection.Forward ? "reaches" : "is reached by";

        Console.WriteLine($"{start.DisplayName}  ({start.Kind}, {start.Module})  {start.Location}");
        Console.WriteLine($"{arrow} {subgraph.Nodes.Count - 1} node(s):");
        Console.WriteLine();

        if (direction == TraversalDirection.Backward)
        {
            PrintRoots(subgraph, startId);
        }

        foreach (var group in subgraph.Nodes
            .Where(n => n.Id != startId)
            .GroupBy(n => n.Kind)
            .OrderBy(g => g.Key))
        {
            Console.WriteLine($"  {group.Key} ({group.Count()})");

            foreach (var node in group
                .OrderBy(n => subgraph.DepthById.GetValueOrDefault(n.Id))
                .ThenBy(n => n.DisplayName, StringComparer.Ordinal))
            {
                var depth = subgraph.DepthById.GetValueOrDefault(node.Id);
                var flags = string.Concat(
                    node.Ambiguous ? " [ambiguous]" : string.Empty,
                    node.Utility ? " [utility]" : string.Empty);

                Console.WriteLine($"    d{depth,-2} {node.DisplayName,-52} {node.Location}{flags}");
            }

            Console.WriteLine();
        }

        PrintDataLayer(subgraph);
    }

    /// <summary>
    /// The entry points that reach this node, first and grouped by what starts them.
    /// <para>
    /// Backward's whole question is "who is affected", so the roots ARE the answer and everything
    /// else is the path to it. Grouping by <see cref="RootKind"/> rather than by node kind is the
    /// load-bearing part: ordering.orders is reached by four endpoints and one background sweeper,
    /// and before this the sweeper sat in a list of eleven Methods with no sign it was an entry
    /// point at all. An incident triaged off that list would have four suspects instead of five.
    /// </para>
    /// </summary>
    private static void PrintRoots(Subgraph subgraph, string startId)
    {
        var roots = subgraph.Nodes
            .Where(n => n.Id != startId && n.RootKind != RootKind.None)
            .ToList();

        if (roots.Count == 0)
        {
            // Not the same as "nothing reaches it": a table written only by an unreferenced helper
            // has callers but no entry point, and that is worth saying out loud.
            Console.WriteLine("  Entry points (0) - nothing reaches this from an endpoint, consumer or background job.");
            Console.WriteLine();
            return;
        }

        var byKind = roots
            .GroupBy(n => n.RootKind)
            .OrderBy(g => g.Key)
            .ToList();

        Console.WriteLine($"  Entry points ({roots.Count}): " + string.Join(" + ", byKind
            .Select(g => $"{g.Count()} {Label(g.Key, g.Count())}")));
        Console.WriteLine();

        foreach (var group in byKind)
        {
            foreach (var node in group
                .OrderBy(n => subgraph.DepthById.GetValueOrDefault(n.Id))
                .ThenBy(n => n.DisplayName, StringComparer.Ordinal))
            {
                var depth = subgraph.DepthById.GetValueOrDefault(node.Id);
                Console.WriteLine(
                    $"    {Label(group.Key, 1),-18} d{depth,-2} {node.DisplayName,-52} {node.Location}");
            }
        }

        Console.WriteLine();
    }

    private static string Label(RootKind kind, int count) => (kind, count) switch
    {
        (RootKind.Endpoint, 1) => "endpoint",
        (RootKind.Endpoint, _) => "endpoints",
        (RootKind.Consumer, 1) => "consumer",
        (RootKind.Consumer, _) => "consumers",
        (RootKind.BackgroundService, 1) => "background job",
        (RootKind.BackgroundService, _) => "background jobs",
        _ => kind.ToString(),
    };

    /// <summary>
    /// The data layer restated on its own, because it is the answer to the question the tool was
    /// built for. Columns are grouped under their table rather than listed flat - a comma-separated
    /// run of fifty qualified names is technically complete and practically unreadable.
    /// </summary>
    private static void PrintDataLayer(Subgraph subgraph)
    {
        // Computed in Core (AnswerBuilder), rendered here. The read/write derivation used to live
        // in this file, which meant the HTTP API could only reuse it by reimplementing it - and two
        // implementations of one rule drift without anyone noticing.
        //
        // Called for BOTH directions, unchanged. That is F10 in docs/known-limitations.md: on a
        // backward trace this block lists the TARGET's own columns, not what the reaching flows
        // write. The API fixes it by carrying no data layer on a backward answer; fixing it here
        // would change output this phase verifies byte-for-byte, so it stays a separate decision.
        var dataLayer = AnswerBuilder.DataLayer(subgraph);

        if (dataLayer.Tables.Count == 0)
        {
            return;
        }

        var width = Math.Min(38, dataLayer.Tables.Max(t => t.Table.Length));

        Console.WriteLine($"  Data layer - {dataLayer.Tables.Count} table(s), " +
                          $"{dataLayer.ColumnCount} column(s):");
        Console.WriteLine();

        foreach (var table in dataLayer.Tables)
        {
            var access = table.Access.Length == 0 ? "  " : table.Access;

            Console.WriteLine(
                $"    {access,-2}  {table.Table.PadRight(width)}  {table.Location}");

            if (table.Columns.Count > 0)
            {
                Console.WriteLine($"          {string.Join(", ", table.Columns.Select(c => c.Name))}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("    W = written  ·  R = read  ·  columns are shown only where a write names one");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- selection

    private static void ReportNoMatch(CodeGraph graph, string selector)
    {
        Console.Error.WriteLine($"error: no node matches \"{selector}\".");

        var near = NodeResolver.NearMatches(graph, selector);

        if (near.Count == 0)
        {
            return;
        }

        Console.Error.WriteLine("Did you mean:");
        foreach (var id in near)
        {
            Console.Error.WriteLine($"  {id}");
        }
    }

    private static string Short(string clrTypeName) =>
        clrTypeName[(clrTypeName.LastIndexOf('.') + 1)..];
}
