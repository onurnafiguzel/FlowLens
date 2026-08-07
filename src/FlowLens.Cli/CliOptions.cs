using FlowLens.Core;

namespace FlowLens.Cli;

public sealed record CliOptions(
    string SolutionPath,
    bool CheckCompilation,
    bool ListMethods,
    bool IncludeTests,
    SemanticDemoTarget DemoTarget)
{
    public const string Usage = """
        Usage:
          flowlens <solution-path> [options]

        Options:
          --check-compilation   Compile every project and report error counts (slow; minutes).
          --list-methods        Print every method declaration as file:line -> Type.Method.
          --include-tests       Include test projects in the main per-module table.
          --demo-project <name> Project for the SemanticModel demo.
          --demo-type <name>    Type for the SemanticModel demo.
          --demo-method <name>  Method for the SemanticModel demo.
          -h, --help            Show this help.
        """;

    public static CliOptions? Parse(string[] args, out string? error)
    {
        error = null;

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            return null;
        }

        string? solutionPath = null;
        var checkCompilation = false;
        var listMethods = false;
        var includeTests = false;
        var demo = SemanticDemoTarget.Default;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
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
                    if (i + 1 >= args.Length)
                    {
                        error = $"{arg} requires a value.";
                        return null;
                    }

                    var value = args[++i];
                    demo = arg switch
                    {
                        "--demo-project" => demo with { ProjectName = value },
                        "--demo-type" => demo with { TypeName = value },
                        _ => demo with { MethodName = value },
                    };
                    break;
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

        return new CliOptions(
            Path.GetFullPath(solutionPath),
            checkCompilation,
            listMethods,
            includeTests,
            demo);
    }
}
