namespace FlowLens.Core.Evals;

/// <param name="Expected">Items the source says must be there.</param>
/// <param name="Actual">Items the answer produced.</param>
/// <param name="FalsePositive">Produced but not expected. Not applicable to presence-only facets.</param>
public sealed record MetricRow(
    string Level,
    EfScope Scope,
    int Expected,
    int Found,
    int Actual,
    int FalsePositive,
    bool PrecisionApplies)
{
    /// <summary>Null when nothing was expected: a recall of "100%" over zero items is not a measurement.</summary>
    public double? Recall => Expected == 0 ? null : (double)Found / Expected;

    public double? Precision =>
        !PrecisionApplies || Actual == 0 ? null : (double)(Actual - FalsePositive) / Actual;
}

public sealed record EvidenceTally(int ExpectedMechanism, int DifferentButValid, int NotFound)
{
    public int Total => ExpectedMechanism + DifferentButValid + NotFound;
}

/// <param name="Representative">False when the class has exactly one example in the graph.</param>
public sealed record CategoryRow(
    string PopulationClass,
    int Questions,
    int PopulationCount,
    bool Representative,
    int Expected,
    int Found,
    int Missed);

/// <param name="Row">Which prediction state the question was in BEFORE the run.</param>
/// <param name="Realized">Whether a miss actually happened.</param>
/// <param name="Questions">Question ids in this cell, ordered.</param>
public sealed record BoxCell(string Row, bool Realized, string Meaning, IReadOnlyList<string> Questions);

/// <param name="Questions">Questions that make this failure class visible. Empty means the Reason must explain why.</param>
public sealed record MetaRow(string Id, IReadOnlyList<string> Questions, string Reason);

public sealed record UnmeasurableClass(string Id, string Name, string Reason);

public sealed record OracleTally(int Confirmed, int Corrected, int Pending);

public sealed record EvalScorecard(
    EvalRun Run,
    IReadOnlyList<MetricRow> Metrics,
    EvidenceTally Evidence,
    IReadOnlyList<CategoryRow> Categories,
    IReadOnlyList<BoxCell> Boxes,
    IReadOnlyList<MetaRow> Meta,
    IReadOnlyList<UnmeasurableClass> Unmeasurable,
    OracleTally Oracle,
    IReadOnlyList<string> Unresolved);

/// <summary>
/// Turns the per-question comparisons into the numbers the report prints.
/// <para>
/// Four separations are load-bearing and none of them may be averaged away: table versus column,
/// write column versus read column, inside EF versus outside EF, and evidence quality versus recall.
/// Phase 3's single "82%" hid a 0% behind a 90%, which is the mistake this class exists not to repeat.
/// </para>
/// </summary>
public static class EvalScore
{
    public const string PredictedOpen = "ongoruldu, sinir ACIK";
    public const string PredictedClosed = "ongoruldu, sinir KAPANMISTI";
    public const string NotPredicted = "ongorulmedi";

    public static EvalScorecard Build(EvalRun run)
    {
        var comparisons = run.Results
            .Where(r => r.Resolved)
            .SelectMany(r => r.Comparisons)
            .ToList();

        return new EvalScorecard(
            run,
            Metrics(comparisons),
            Evidence(run),
            Categories(run),
            Boxes(run),
            Meta(run),
            Unmeasurable(),
            Oracle(run),
            [.. run.Results.Where(r => !r.Resolved).Select(r => $"{r.Question.Id}: {r.ResolutionError}")]);
    }

    // ---------------------------------------------------------------- metrics

    private static readonly (FacetKind Kind, string Label, bool Split)[] Levels =
    [
        (FacetKind.Table, "tablo", true),
        (FacetKind.ColumnWrite, "kolon-yazma", true),
        (FacetKind.ColumnRead, "kolon-okuma", true),
        (FacetKind.Root, "kok", false),
        (FacetKind.Event, "event", false),
        (FacetKind.ExternalStore, "dis depo", false),
        (FacetKind.Node, "dugum", false),
        (FacetKind.Limitation, "sinir kodu", false),
    ];

    private static IReadOnlyList<MetricRow> Metrics(IReadOnlyList<Comparison> comparisons)
    {
        var rows = new List<MetricRow>();

        foreach (var (kind, label, split) in Levels)
        {
            var forKind = comparisons.Where(c => c.Kind == kind).ToList();

            if (forKind.Count == 0)
            {
                continue;
            }

            if (split)
            {
                foreach (var scope in (EfScope[])[EfScope.Inside, EfScope.Outside])
                {
                    var subset = forKind.Where(c => c.Scope == scope).ToList();

                    if (subset.Count > 0)
                    {
                        rows.Add(Row(label, scope, subset));
                    }
                }

                // The R/W mismatch rows carry EfScope.None and belong to the table level.
                var access = forKind.Where(c => c.Scope == EfScope.None).ToList();

                if (access.Count > 0)
                {
                    rows.Add(Row(label + " (erisim)", EfScope.None, access));
                }

                continue;
            }

            rows.Add(Row(label, EfScope.None, forKind));
        }

        return rows;

        static MetricRow Row(string label, EfScope scope, IReadOnlyList<Comparison> subset) =>
            new(
                label,
                scope,
                subset.Sum(c => c.ExpectedCount),
                subset.Sum(c => c.FoundCount),
                subset.Sum(c => c.ActualCount),
                subset.Sum(c => c.Unexpected.Count),
                subset.All(c => !c.PresenceOnly));
    }

    // ---------------------------------------------------------------- evidence

    private static EvidenceTally Evidence(EvalRun run)
    {
        var items = run.Results.SelectMany(r => r.Evidence).ToList();

        return new EvidenceTally(
            items.Count(i => i.Outcome == EvidenceOutcome.ExpectedMechanism),
            items.Count(i => i.Outcome == EvidenceOutcome.DifferentButValid),
            items.Count(i => i.Outcome == EvidenceOutcome.NotFound));
    }

    // ---------------------------------------------------------------- categories

    private static IReadOnlyList<CategoryRow> Categories(EvalRun run) =>
    [
        .. run.Results
            .GroupBy(r => r.Question.Population.Class, StringComparer.Ordinal)
            .OrderBy(g => g.Key.Length)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CategoryRow(
                g.Key,
                g.Count(),
                g.Max(r => r.Question.Population.Count),
                g.All(r => r.Question.Population.Representative),
                g.SelectMany(r => r.Comparisons).Sum(c => c.ExpectedCount),
                g.SelectMany(r => r.Comparisons).Sum(c => c.FoundCount),
                g.SelectMany(r => r.Comparisons).Sum(c => c.Missing.Count))),
    ];

    // ---------------------------------------------------------------- 3x2 boxes

    /// <summary>
    /// The unit is the QUESTION, not the prediction. A prediction cannot be attributed to a specific
    /// miss without a mapping the graph does not carry, and pretending otherwise would credit seven
    /// predictions for one loss. The per-question table below the boxes carries the individual ids so
    /// attribution stays possible by hand.
    /// </summary>
    private static IReadOnlyList<BoxCell> Boxes(EvalRun run)
    {
        var cells = new List<BoxCell>();

        foreach (var (row, meaningRealized, meaningNot) in (ValueTuple<string, string, string>[])
        [
            (PredictedOpen, "beklenen acik sinir - teyit", "ONGORU BASTAN YANLISTI - bulgu"),
            (PredictedClosed, "REGRESYON - kapanmis sinir geri acildi", "kapanis korunuyor"),
            (NotPredicted, "BU FAZIN ASIL BULGUSU", "normal"),
        ])
        {
            foreach (var realized in (bool[])[true, false])
            {
                cells.Add(new BoxCell(
                    row,
                    realized,
                    realized ? meaningRealized : meaningNot,
                    [
                        .. run.Results
                            .Where(r => r.Resolved && RowOf(r) == row && r.MissRealized == realized)
                            .Select(r => r.Question.Id)
                            .Order(StringComparer.Ordinal),
                    ]));
            }
        }

        return cells;
    }

    private static string RowOf(QuestionResult result)
    {
        var predictions = result.Question.ExpectedToFail;

        if (predictions.Count == 0)
        {
            return NotPredicted;
        }

        return predictions.Any(p => p.ClosedIn is { Length: > 0 }) ? PredictedClosed : PredictedOpen;
    }

    // ---------------------------------------------------------------- meta-test

    /// <summary>
    /// Failure classes made visible by an EXPECTED VALUE rather than by a prediction.
    /// <para>
    /// F3 is the clearest example: the row-level rule is not predicted to fail anywhere, it is
    /// asserted - Q01/Q02/Q14 expect exactly the columns it produces, so a regression shows up as a
    /// miss with no prediction behind it. Deriving this from expectedToFail alone would leave every
    /// closed limitation looking untested.
    /// </para>
    /// </summary>
    private static readonly (string Id, string[] Questions)[] VisibleByExpectation =
    [
        ("F1", ["Q06"]),
        ("F3", ["Q01", "Q02", "Q14"]),
        ("F8", ["Q16", "Q17", "Q18", "Q19", "Q20", "Q21", "Q22"]),
        ("L1", ["Q01", "Q02", "Q03", "Q04", "Q05", "Q06", "Q07", "Q08"]),
        ("L3", ["Q09", "Q10", "Q13", "Q17", "Q18"]),
        ("L4", ["Q09", "Q10", "Q11", "Q13", "Q15"]),
        ("L7", ["Q08"]),
        ("L9", ["Q03", "Q14"]),
        ("L11", ["Q09", "Q18"]),
        ("L13", ["Q01", "Q06"]),
        ("L15", ["Q01", "Q13", "Q21"]),
        ("L16", ["Q01", "Q02", "Q03", "Q13", "Q14"]),

        // L24 is visible through Q16's limitations expectation, not through a prediction: the
        // question asserts that a backward answer must carry the raw-sql warning, and it does not.
        ("L24", ["Q16"]),
    ];

    /// <summary>
    /// Rows that are deliberately empty, each with the reason. A silent gap in this table is exactly
    /// the shape of the four silent errors Phase 3 shipped, so an empty row without a reason fails a
    /// test rather than passing quietly.
    /// </summary>
    private static readonly (string Id, string Reason)[] ReasonedBlanks =
    [
        ("F10", "Yapisal: backward cevabi DataLayer TASIMIYOR (tip seviyesinde null). "
            + "EvalTests.ABackwardAnswerCarriesNoDataLayer bunu popülasyon uzerinde sabitliyor."),
        ("L2", "Faz 3'te konusuz kaldi: DbSet erisimi ifadenin TIPINDEN cozuluyor, accessor'in seklinden degil."),
        ("L8", "Yapisal garanti, cevap dogrulugu degil. Mevcut suite'te "
            + "ThinningUtilityNodesNeverChangesWhatIsReachable 41 sorguda sabitliyor."),
        ("L10", "Olculdu: 4 site (CheckoutHandler.cs:60,175 - CardPaymentStrategy.cs:45,50). "
            + "Hicbiri bir tabloyu, kolonu, koku ya da event'i degistirmiyor - cevap duzeyinde "
            + "olculebilir etkisi YOK, dolayisiyla onu gorunur kilan bir soru YAZILAMAZ."),
        ("L12", "Tek ornek (CardPaymentStrategy). Popülasyon 1: kategori degil yalniz o ornek olculebilirdi."),
        ("L14", "Ortam kosulu (EF surum kapisi). EfPreflight build'i durdurur; cevap dogrulugu sorusu degil."),
        ("L20", "Calisma zamani olgusu (JIT inlining). Statik eval yapisal olarak goremez."),
    ];

    private static IReadOnlyList<MetaRow> Meta(EvalRun run)
    {
        var byId = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        void Credit(string id, string question)
        {
            if (!byId.TryGetValue(id, out var set))
            {
                byId[id] = set = new SortedSet<string>(StringComparer.Ordinal);
            }

            set.Add(question);
        }

        foreach (var result in run.Results)
        {
            foreach (var prediction in result.Question.ExpectedToFail)
            {
                Credit(prediction.Id, result.Question.Id);

                // "L16-4" also credits L16: the sub-item is how the limitation is written down, the
                // row is how it is tracked.
                var dash = prediction.Id.IndexOf('-');

                if (dash > 0)
                {
                    Credit(prediction.Id[..dash], result.Question.Id);
                }
            }
        }

        foreach (var (id, questions) in VisibleByExpectation)
        {
            foreach (var question in questions)
            {
                Credit(id, question);
            }
        }

        var blanks = ReasonedBlanks.ToDictionary(b => b.Id, b => b.Reason, StringComparer.Ordinal);

        var ids = Enumerable.Range(1, 10).Select(i => $"F{i}")
            .Concat(Enumerable.Range(1, 24).Select(i => $"L{i}"))
            .ToList();

        return
        [
            .. ids.Select(id => new MetaRow(
                id,
                byId.TryGetValue(id, out var questions) ? [.. questions] : [],
                byId.ContainsKey(id) ? string.Empty : blanks.GetValueOrDefault(id, string.Empty))),
        ];
    }

    // ---------------------------------------------------------------- unmeasurable

    /// <summary>
    /// Classes with a population of ZERO in the target. Printed as their own rows rather than
    /// omitted: an absent row reads as "covered", and roadmap rule 8 forbids exactly that.
    /// </summary>
    private static IReadOnlyList<UnmeasurableClass> Unmeasurable() =>
    [
        new("P15", "reflection", "Hedef repo Activator.CreateInstance / Type.GetMethod().Invoke KULLANMIYOR. Popülasyon 0."),
        new("P16", "dynamic-dispatch", "Hedef repo 'dynamic' KULLANMIYOR. Popülasyon 0."),
        new("P17", "inlining (L20)", "Calisma zamani olgusu; statik eval goremez. Faz 6 adim 0b'de olculdu: 97/255 senkron dugum risk altinda."),
        new("P14", "delegate / Polly (L12)", "Tek site (CardPaymentStrategy). Tek ornek bir kategori olusturmaz."),
    ];

    // ---------------------------------------------------------------- oracle

    private static OracleTally Oracle(EvalRun run)
    {
        var realized = run.Results.Where(r => r.Resolved && r.MissRealized).ToList();

        return new OracleTally(
            realized.Count(r => r.OracleVerdict == EvalOracle.Confirmed),
            realized.Count(r => r.OracleVerdict == EvalOracle.Corrected),
            realized.Count(r => r.OracleVerdict == EvalOracle.Pending));
    }
}
