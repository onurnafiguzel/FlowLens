using FlowLens.Core;
using FlowLens.Core.Docs;

namespace FlowLens.Cli;

/// <summary>
/// Generates the documentation site from graph.json.
/// <para>
/// No solution load, no LLM, no Node: the pages are a pure function of the graph. Verification of
/// the emitted Mermaid is a separate, optional step (tools/mermaid-check) precisely so that
/// producing documentation never depends on a JavaScript toolchain being installed.
/// </para>
/// </summary>
public static class DocsCommand
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

        var output = Path.GetFullPath(options.OutputDirectory ?? "docs-out");

        Console.WriteLine($"      graph : {source.Path}");
        Console.WriteLine($"      cikti : {output}");

        if (options.EndpointSelector is { } endpoint)
        {
            Console.WriteLine($"      filtre: endpoint = {endpoint}");
        }

        if (options.ModuleFilter is { } module)
        {
            Console.WriteLine($"      filtre: module = {module}");
        }

        Console.WriteLine();

        var result = DocsSite.Write(
            snapshot.Graph,
            snapshot.Diagnostics,
            new DocsRequest(output, options.EndpointSelector, options.ModuleFilter));

        if (result.Flows == 0 && result.Modules == 0)
        {
            // An empty run with a filter means the filter matched nothing. Reporting success here
            // would be a silent empty result - the shape this project treats as the worst failure.
            Console.Error.WriteLine("error: filtre hicbir seyle eslesmedi, dosya uretilmedi.");
            return Runner.ExitNotFound;
        }

        Console.WriteLine($"      {result.Files.Count} dosya: {result.Flows} akis, {result.Modules} modul" +
                          $"{(result.IndexWritten ? ", 1 bagimlilik grafigi, 1 index" : string.Empty)}");

        if (!result.IndexWritten)
        {
            // A README built from a filtered run would link to files that were not written.
            Console.WriteLine();
            Console.WriteLine("      not: filtre kullanildigi icin README.md YAZILMADI - kismi bir index");
            Console.WriteLine("           olmayan dosyalara baglanti verirdi. Tam uretim: flowlens docs -o <dir>");
        }

        Console.WriteLine();
        return Runner.ExitOk;
    }
}
