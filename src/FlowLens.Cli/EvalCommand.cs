using System.Text.Json;
using FlowLens.Core;
using FlowLens.Core.Evals;

namespace FlowLens.Cli;

/// <summary>
/// Scores the eval questions against graph.json.
/// <para>
/// The question set is READ ONLY. Nothing here writes to it, and that is the point: the expected
/// values were committed before this file existed, so "was an expected value adjusted to match the
/// output?" is answerable from git history alone.
/// </para>
/// </summary>
public static class EvalCommand
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
            Console.Error.WriteLine("      Cozum: flowlens build <solution-path> -o graph.json");
            return Runner.ExitNotFound;
        }

        var questionsPath = Path.GetFullPath(
            options.QuestionsPath ?? EvalQuestionFile.DefaultQuestionsPath);

        if (!File.Exists(questionsPath))
        {
            Console.Error.WriteLine($"error: soru seti yok: {questionsPath}");
            Console.Error.WriteLine(
                "      Eval, elle yazilmis beklenen degerler olmadan calismaz - kendi ciktisindan " +
                "beklenen deger uretmez.");
            return Runner.ExitNotFound;
        }

        EvalQuestionSet set;

        try
        {
            set = EvalQuestionFile.Read(questionsPath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"error: {questionsPath} okunamadi: {ex.Message}");
            return Runner.ExitUsage;
        }

        var verdictsPath = Path.GetFullPath(
            options.VerdictsPath ?? EvalQuestionFile.DefaultVerdictsPath);

        var verdicts = EvalQuestionFile.ReadVerdicts(verdictsPath);

        var run = EvalRunner.Run(
            snapshot.Graph,
            snapshot.Diagnostics,
            source.Path,
            set,
            verdicts,
            new TraversalQuery(options.MaxDepth, options.IncludeUtility));

        var card = EvalScore.Build(run);

        var rendered = options.JsonOutput
            ? JsonSerializer.Serialize(card, JsonOptions)
            : EvalMarkdown.Render(card);

        if (options.OutputDirectory is { Length: > 0 } path)
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
            File.WriteAllText(full, rendered.ReplaceLineEndings("\n"));
            Console.WriteLine($"      sorular : {questionsPath} ({set.Questions.Count})");
            Console.WriteLine($"      rapor   : {full}");
        }
        else
        {
            Console.WriteLine();
            Console.Write(rendered);
            Console.WriteLine();
        }

        Summarise(card);

        // A broken selector is a broken QUESTION, not a miss, and it must not be reported as a
        // measured result: an unrunnable question silently scoring zero would look like a recall
        // loss the tool caused.
        if (card.Unresolved.Count > 0)
        {
            return Runner.ExitNotFound;
        }

        // Realised misses are the expected state of this phase - the report says which are predicted
        // and which are not. Exit 3 marks "ran, knowingly incomplete", never "failed".
        return card.Oracle.Pending > 0 ? Runner.ExitIncomplete : Runner.ExitOk;
    }

    private static void Summarise(EvalScorecard card)
    {
        Console.WriteLine();

        foreach (var row in card.Metrics)
        {
            var scope = row.Scope == EfScope.None ? string.Empty : $" ({(row.Scope == EfScope.Inside ? "EF ici" : "EF disi")})";

            Console.WriteLine(
                $"      {row.Level + scope,-28} beklenen {row.Expected,4}  bulunan {row.Found,4}  " +
                $"fazladan {row.FalsePositive,3}");
        }

        Console.WriteLine();

        var realized = card.Run.Results.Count(r => r.Resolved && r.MissRealized);
        var surprise = card.Boxes.First(b => b.Row == EvalScore.NotPredicted && b.Realized);

        Console.WriteLine($"      kacirma gerceklesen soru : {realized}/{card.Run.Results.Count}");
        Console.WriteLine($"      ONGORULMEYEN kacirma     : {surprise.Questions.Count}"
            + (surprise.Questions.Count == 0 ? string.Empty : $" -> {string.Join(", ", surprise.Questions)}"));
        Console.WriteLine($"      oracle beklemede         : {card.Oracle.Pending}");
    }

    /// <summary>Same shape as graph.json, the HTTP API and the triage report.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
