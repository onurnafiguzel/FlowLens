using FlowLens.Core;

namespace FlowLens.Cli;

public enum CliCommand
{
    /// <summary>Phase 1 behaviour: load, count methods, demo the SemanticModel.</summary>
    Scan,

    /// <summary>Phase 2: discover endpoints and report eliminated/unresolved candidates.</summary>
    Endpoints,

    /// <summary>Phase 2: walk the call chain from one endpoint.</summary>
    Trace,
}

public sealed record CliOptions(
    CliCommand Command,
    string SolutionPath,
    bool CheckCompilation,
    bool ListMethods,
    bool IncludeTests,
    SemanticDemoTarget DemoTarget,
    string? EndpointSelector,
    int MaxDepth,
    ImplementationPolicy ImplementationPolicy)
{
    public const string Usage = """
        Usage:
          flowlens <solution-path> [scan options]          Phase 1: load, count, SemanticModel demo
          flowlens endpoints <solution-path>               Phase 2: list discovered endpoints
          flowlens trace <solution-path> --endpoint "..."  Phase 2: walk one endpoint's call chain

        Scan options:
          --check-compilation   Compile every project and report error counts.
          --list-methods        Print every method declaration as file:line -> Type.Method.
          --include-tests       Include test projects in the main per-module table.
          --demo-project <name> Project for the SemanticModel demo.
          --demo-type <name>    Type for the SemanticModel demo.
          --demo-method <name>  Method for the SemanticModel demo.

        Trace options:
          --endpoint "<METHOD /route>"  Endpoint to start from, e.g. "POST /api/ordering/checkout".
          --max-depth <n>               Traversal depth limit (default 20; longest measured chain is 10).
          --implementation-policy <p>   all (default) | declaring-module. How an interface call
                                        resolves to concrete implementations.

          -h, --help            Show this help.
        """;

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
        }

        string? solutionPath = null;
        var checkCompilation = false;
        var listMethods = false;
        var includeTests = false;
        var demo = SemanticDemoTarget.Default;
        string? endpointSelector = null;
        var maxDepth = 20;
        var implementationPolicy = ImplementationPolicy.AllImplementations;

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
                case "--demo-project":
                case "--demo-type":
                case "--demo-method":
                case "--endpoint":
                case "--max-depth":
                case "--implementation-policy":
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

                    if (solutionPath is not null)
                    {
                        error = $"Unexpected extra argument: {arg}";
                        return null;
                    }

                    solutionPath = arg;
                    break;
            }
        }

        if (solutionPath is null)
        {
            error = "A solution path is required.";
            return null;
        }

        if (command == CliCommand.Trace && string.IsNullOrWhiteSpace(endpointSelector))
        {
            error = "trace requires --endpoint, e.g. --endpoint \"POST /api/ordering/checkout\".";
            return null;
        }

        return new CliOptions(
            command,
            Path.GetFullPath(solutionPath),
            checkCompilation,
            listMethods,
            includeTests,
            demo,
            endpointSelector,
            maxDepth,
            implementationPolicy);
    }
}
