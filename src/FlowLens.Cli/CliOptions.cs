using FlowLens.Core;

namespace FlowLens.Cli;

public enum CliCommand
{
    /// <summary>Phase 1 behaviour: load, count methods, demo the SemanticModel.</summary>
    Scan,

    /// <summary>Phase 2: discover endpoints and report eliminated/unresolved candidates.</summary>
    Endpoints,

    /// <summary>Phase 2 (live) or Phase 3 (over graph.json): walk from one node.</summary>
    Trace,

    /// <summary>Phase 3: build the whole graph and write graph.json.</summary>
    Build,

    /// <summary>Phase 5: generate the documentation site from graph.json.</summary>
    Docs,
}

/// <param name="SolutionPath">Empty when tracing over a graph file, which needs no solution.</param>
/// <param name="GraphPath">Output path for build; input path for a graph-backed trace.</param>
public sealed record CliOptions(
    CliCommand Command,
    string SolutionPath,
    bool CheckCompilation,
    bool ListMethods,
    bool IncludeTests,
    SemanticDemoTarget DemoTarget,
    string? EndpointSelector,
    int MaxDepth,
    ImplementationPolicy ImplementationPolicy,
    string? GraphPath,
    TraversalDirection Direction,
    bool IncludeUtility,
    string? OutputDirectory = null,
    string? ModuleFilter = null)
{
    public const string DefaultGraphPath = "graph.json";

    /// <summary>
    /// True when trace should read graph.json instead of loading the solution. Phase 2's live
    /// trace is kept as-is - it is still the fastest way to see one chain with full Roslyn detail -
    /// so the two coexist and the presence of --graph selects between them.
    /// </summary>
    public bool TracesGraphFile => Command == CliCommand.Trace && GraphPath is not null;

    public const string Usage = """
        Usage:
          THE DATA-LAYER ANSWER - tables and columns. Two steps, and the second is the one
          Phase 4's GET /trace will call:

            flowlens build <solution-path> [-o graph.json]  Build the graph. Do this ONCE.
            flowlens trace "<node>"                         What does this reach?  -> tables + columns
            flowlens trace "<node>" --direction backward    What reaches this?     -> entry points
            flowlens docs -o <dir>                          Mermaid + markdown, from graph.json

              e.g.  flowlens build C:\src\ModularCommerce\ModularCommerce.sln
                    flowlens trace "POST /api/ordering/checkout"
                    flowlens trace "table:ordering.orders" --direction backward

          Everything below is an earlier phase kept for inspection. None of it reports
          tables or columns.

            flowlens <solution-path> [scan options]         Phase 1: load, count, SemanticModel demo
            flowlens endpoints <solution-path>              Phase 2: list discovered endpoints
            flowlens trace <solution-path> --endpoint "..." Phase 2: walk one endpoint LIVE.
                                                            Call chain only - NO tables, NO columns.

          Both live under the word "trace" and the ARGUMENT decides which runs: a solution path
          selects the Phase 2 live walk, anything else traverses graph.json.

        Scan options:
          --check-compilation   Compile every project and report error counts.
          --list-methods        Print every method declaration as file:line -> Type.Method.
          --include-tests       Include test projects in the main per-module table.
          --demo-project <name> Project for the SemanticModel demo.
          --demo-type <name>    Type for the SemanticModel demo.
          --demo-method <name>  Method for the SemanticModel demo.

        Build options:
          -o, --output <path>   Where to write the graph (default graph.json).

        Docs options:
          -o, --output <dir>    Where to write the site (default docs-out).
          --graph <path>        Graph to read (default: searched, see /graph/stats).
          --endpoint "<route>"  Only this flow. README is NOT written for a filtered run.
          --module <name>       Only this module and its flows.

        Trace options:
          --endpoint "<METHOD /route>"  Endpoint to start from, e.g. "POST /api/ordering/checkout".
          --graph <path>                Graph file to traverse (default graph.json). Given a
                                        solution path instead, trace runs Phase 2's live walk.
          --direction forward|backward  forward (default) = what this reaches;
                                        backward = what reaches it.
          --no-utility                  Drop Shared.Kernel plumbing (Result.Success and friends).
          --max-depth <n>               Traversal depth limit (default 20; longest measured chain is 10).
          --implementation-policy <p>   all (default) | declaring-module. How an interface call
                                        resolves to concrete implementations.

          -h, --help            Show this help.

        The trace target may be a route ("POST /api/ordering/checkout") or a node id
        ("table:ordering.orders", "entity:...Order").
        """;

    /// <summary>
    /// Distinguishes "trace this solution" from "trace this node". Extension-based on purpose: a
    /// node selector is a route or a prefixed id, never a path, so the two can never be confused.
    /// </summary>
    private static bool IsSolutionPath(string value) =>
        value.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    public static CliOptions? Parse(string[] args, out string? error)
    {
        error = null;

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            return null;
        }

        var command = CliCommand.Scan;
        var index = 0;

        switch (args[0])
        {
            case "trace":
                command = CliCommand.Trace;
                index = 1;
                break;
            case "endpoints":
                command = CliCommand.Endpoints;
                index = 1;
                break;
            case "build":
                command = CliCommand.Build;
                index = 1;
                break;
            case "docs":
                command = CliCommand.Docs;
                index = 1;
                break;
        }

        var positional = new List<string>();
        var checkCompilation = false;
        var listMethods = false;
        var includeTests = false;
        var demo = SemanticDemoTarget.Default;
        string? endpointSelector = null;
        var maxDepth = 20;
        var implementationPolicy = ImplementationPolicy.AllImplementations;
        string? graphPath = null;
        string? outputPath = null;
        string? moduleFilter = null;
        var direction = TraversalDirection.Forward;
        var includeUtility = true;

        for (; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--check-compilation":
                    checkCompilation = true;
                    break;
                case "--list-methods":
                    listMethods = true;
                    break;
                case "--include-tests":
                    includeTests = true;
                    break;
                case "--no-utility":
                    includeUtility = false;
                    break;
                case "--demo-project":
                case "--demo-type":
                case "--demo-method":
                case "--endpoint":
                case "--max-depth":
                case "--implementation-policy":
                case "--graph":
                case "--direction":
                case "--module":
                case "-o":
                case "--output":
                {
                    if (index + 1 >= args.Length)
                    {
                        error = $"{arg} requires a value.";
                        return null;
                    }

                    var value = args[++index];
                    switch (arg)
                    {
                        case "--demo-project": demo = demo with { ProjectName = value }; break;
                        case "--demo-type": demo = demo with { TypeName = value }; break;
                        case "--demo-method": demo = demo with { MethodName = value }; break;
                        case "--endpoint": endpointSelector = value; break;
                        case "--graph": graphPath = value; break;
                        case "--module": moduleFilter = value; break;
                        case "-o":
                        case "--output": outputPath = value; break;
                        case "--direction":
                            switch (value)
                            {
                                case "forward": direction = TraversalDirection.Forward; break;
                                case "backward": direction = TraversalDirection.Backward; break;
                                default:
                                    error = $"Unknown direction: {value}. Use forward or backward.";
                                    return null;
                            }

                            break;
                        case "--max-depth":
                            if (!int.TryParse(value, out maxDepth) || maxDepth < 1)
                            {
                                error = "--max-depth must be a positive integer.";
                                return null;
                            }

                            break;
                        case "--implementation-policy":
                            switch (value)
                            {
                                case "all":
                                    implementationPolicy = ImplementationPolicy.AllImplementations;
                                    break;
                                case "declaring-module":
                                    implementationPolicy = ImplementationPolicy.DeclaringModuleOnly;
                                    break;
                                default:
                                    error = $"Unknown implementation policy: {value}";
                                    return null;
                            }

                            break;
                    }

                    break;
                }

                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"Unknown option: {arg}";
                        return null;
                    }

                    positional.Add(arg);
                    break;
            }
        }

        if (positional.Count > 1)
        {
            error = $"Unexpected extra argument: {positional[1]}";
            return null;
        }

        var argument = positional.Count == 1 ? positional[0] : null;
        var solutionPath = argument;

        if (command == CliCommand.Trace)
        {
            // Which of the two traces did they mean? The argument decides: a solution path is the
            // Phase 2 live walk, anything else is a node in a built graph. Graph-backed is the
            // default because it is the one that answers the impact question - it carries tables
            // and columns, and it answers in milliseconds instead of reloading the solution.
            var argumentIsSolution = argument is not null && IsSolutionPath(argument);

            if (graphPath is not null || !argumentIsSolution)
            {
                graphPath ??= DefaultGraphPath;
                endpointSelector ??= argument;
                solutionPath = null;

                if (string.IsNullOrWhiteSpace(endpointSelector))
                {
                    error = "trace needs a node, e.g. flowlens trace \"POST /api/ordering/checkout\".";
                    return null;
                }
            }
            else if (string.IsNullOrWhiteSpace(endpointSelector))
            {
                error = "trace over a solution requires --endpoint, " +
                        "e.g. --endpoint \"POST /api/ordering/checkout\".";
                return null;
            }
        }

        // Docs reads a built graph and writes files; it never loads a solution, so a solution path
        // is neither required nor accepted here.
        if (command == CliCommand.Docs)
        {
            solutionPath = null;

            if (argument is not null)
            {
                error = "docs takes no positional argument. Use -o <dir> and optionally " +
                        "--endpoint / --module.";
                return null;
            }
        }
        else if (command != CliCommand.Trace || graphPath is null)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                error = "A solution path is required.";
                return null;
            }
        }

        return new CliOptions(
            command,
            solutionPath is null ? string.Empty : Path.GetFullPath(solutionPath),
            checkCompilation,
            listMethods,
            includeTests,
            demo,
            endpointSelector,
            maxDepth,
            implementationPolicy,
            command == CliCommand.Build ? outputPath ?? DefaultGraphPath : graphPath,
            direction,
            includeUtility,
            command == CliCommand.Docs ? outputPath : null,
            moduleFilter);
    }
}
