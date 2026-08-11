using FlowLens.Core.Answers;

namespace FlowLens.Core.Evals;

/// <summary>Which axis of the answer a comparison is about. Each one is scored on its own row.</summary>
public enum FacetKind
{
    Table,
    ColumnWrite,
    ColumnRead,
    Root,
    Event,
    ExternalStore,
    Limitation,
    Node,
}

/// <summary>
/// Whether the target sits inside EF Core's reach or outside it.
/// <para>
/// Phase 3 measured 90% table recall inside EF and 0% outside, averaging to 82% - a number that hid
/// both halves. They are never averaged here.
/// </para>
/// </summary>
public enum EfScope
{
    /// <summary>Not applicable to this facet (roots, events, stores).</summary>
    None,

    Inside,
    Outside,
}

/// <param name="PresenceOnly">
/// True for limitations: the questions name codes that MUST appear, not the complete set an answer
/// may carry, so an unlisted code is not a false positive.
/// </param>
public sealed record Comparison(
    FacetKind Kind,
    EfScope Scope,
    string Subject,
    IReadOnlyList<string> Expected,
    IReadOnlyList<string> Actual,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unexpected,
    bool PresenceOnly)
{
    public int ExpectedCount => Expected.Count;

    public int FoundCount => Expected.Count - Missing.Count;

    public int ActualCount => Actual.Count;
}

/// <summary>
/// How strong the evidence behind a correct answer was. Three outcomes, never two: a right answer
/// carried by second-class evidence is right in recall and wrong in evidence, and one bucket would
/// have to lie about one of them.
/// </summary>
public enum EvidenceOutcome
{
    ExpectedMechanism,
    DifferentButValid,
    NotFound,
}

public sealed record EvidenceItem(
    string Table,
    string Column,
    EvidenceOutcome Outcome,
    string Mechanisms);

/// <param name="ResolutionError">Empty when the selector resolved. A broken selector is a broken QUESTION, reported apart from a miss.</param>
/// <param name="MissRealized">True when any asserted facet lost recall or precision.</param>
public sealed record QuestionResult(
    EvalQuestion Question,
    string ResolvedId,
    string ResolutionError,
    IReadOnlyList<Comparison> Comparisons,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<string> Misses,
    IReadOnlyList<string> FalsePositives,
    string OracleVerdict,
    string OracleEvidence)
{
    public bool Resolved => ResolutionError.Length == 0;

    public bool MissRealized => Misses.Count > 0 || FalsePositives.Count > 0;
}

public sealed record EvalRun(
    EvalQuestionSet Set,
    string GraphPath,
    int GraphNodes,
    int GraphEdges,
    IReadOnlyList<QuestionResult> Results,
    IReadOnlyList<string> EfOutsideTables);

/// <summary>
/// Runs every question through <see cref="AnswerBuilder"/> and compares the answer to the truth
/// written in questions.json.
/// <para>
/// No second traversal and no second answer model: the eval asks exactly what the CLI, the HTTP API,
/// the documentation generator and triage ask, because measuring anything else would measure a
/// surface no user has.
/// </para>
/// </summary>
public static class EvalRunner
{
    /// <summary>
    /// Mechanisms that mean EF Core itself issues SQL for the table: a DbSet call, a fluent chain, an
    /// interceptor, an owned-row write. Construction and change-tracker inference are deliberately NOT
    /// here - they establish that a flow touches an entity, not that EF wrote its table.
    /// <para>
    /// Measured over the committed graph: exactly one of sixteen tables has none of these
    /// (<c>discovery.product_embeddings</c>, reached only through construction because every real
    /// access is raw SQL). That is precisely the split Phase 3 reported as "EF inside / EF outside",
    /// derived here rather than hand-listed, so it follows the graph if the graph changes.
    /// </para>
    /// </summary>
    private static readonly HashSet<EdgeMechanism> EfIssuedMechanisms =
    [
        EdgeMechanism.DbSetProperty,
        EdgeMechanism.SetOfT,
        EdgeMechanism.FluentChainHead,
        EdgeMechanism.ExecuteUpdateSetProperty,
        EdgeMechanism.SaveChangesInterceptor,
        EdgeMechanism.OwnedCollectionAdd,
        EdgeMechanism.RowInsert,
    ];

    public static EvalRun Run(
        CodeGraph graph,
        IReadOnlyList<string> diagnostics,
        string graphPath,
        EvalQuestionSet set,
        IReadOnlyDictionary<string, OracleVerdict> verdicts,
        TraversalQuery? query = null)
    {
        var traversal = query ?? new TraversalQuery();
        var scopes = ClassifyTables(graph);

        var results = set.Questions
            .OrderBy(q => q.Id, StringComparer.Ordinal)
            .Select(q => Evaluate(graph, diagnostics, q, traversal, scopes, verdicts))
            .ToList();

        var outside = scopes
            .Where(kv => kv.Value == EfScope.Outside)
            .Select(kv => kv.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new EvalRun(set, graphPath, graph.Nodes.Count, graph.Edges.Count, results, outside);
    }

    // ---------------------------------------------------------------- one question

    private static QuestionResult Evaluate(
        CodeGraph graph,
        IReadOnlyList<string> diagnostics,
        EvalQuestion question,
        TraversalQuery query,
        IReadOnlyDictionary<string, EfScope> scopes,
        IReadOnlyDictionary<string, OracleVerdict> verdicts)
    {
        var id = NodeResolver.Resolve(graph, question.Selector.Node);

        if (id is null)
        {
            var near = NodeResolver.NearMatches(graph, question.Selector.Node, 5);

            return new QuestionResult(
                question,
                string.Empty,
                $"selector cozulemedi: \"{question.Selector.Node}\""
                    + (near.Count == 0 ? string.Empty : $" (yakin: {string.Join(" | ", near)})"),
                [],
                [],
                [],
                [],
                EvalOracle.Pending,
                string.Empty);
        }

        var forward = question.Direction == TraversalDirection.Forward;

        var subgraph = forward
            ? graph.ForwardSubgraph(id, query)
            : graph.BackwardSubgraph(id, query);

        var answer = AnswerBuilder.Build(graph, subgraph, id, question.Direction, query, diagnostics);

        var comparisons = new List<Comparison>();
        var evidence = new List<EvidenceItem>();

        CompareTables(question, answer, scopes, comparisons, evidence);
        CompareRoots(question, answer, comparisons);
        CompareEvents(graph, question, answer, comparisons);
        CompareExternalStores(question, answer, comparisons);
        CompareLimitations(question, answer, comparisons);
        CompareNodes(question, answer, comparisons);

        var misses = comparisons
            .SelectMany(c => c.Missing.Select(m => $"{Label(c.Kind)}: {m}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        var falsePositives = comparisons
            .SelectMany(c => c.Unexpected.Select(m => $"{Label(c.Kind)}: {m}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        var verdict = verdicts.GetValueOrDefault(question.Id);

        return new QuestionResult(
            question,
            id,
            string.Empty,
            comparisons,
            evidence,
            misses,
            falsePositives,
            misses.Count == 0 && falsePositives.Count == 0
                ? string.Empty
                : verdict?.Verdict ?? EvalOracle.Pending,
            verdict?.SourceEvidence ?? string.Empty);
    }

    // ---------------------------------------------------------------- facets

    private static void CompareTables(
        EvalQuestion question,
        TraceAnswer answer,
        IReadOnlyDictionary<string, EfScope> scopes,
        List<Comparison> comparisons,
        List<EvidenceItem> evidence)
    {
        if (question.Expected.Tables is not { } expected)
        {
            return;
        }

        var actual = answer.DataLayer?.Tables ?? [];
        var actualByName = actual.ToDictionary(t => t.Table, StringComparer.Ordinal);

        foreach (var scope in (EfScope[])[EfScope.Inside, EfScope.Outside])
        {
            var expectedNames = expected
                .Where(t => Scope(scopes, t.Name) == scope)
                .Select(t => t.Name)
                .Order(StringComparer.Ordinal)
                .ToList();

            var actualNames = actual
                .Select(t => t.Table)
                .Where(name => Scope(scopes, name) == scope)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (expectedNames.Count == 0 && actualNames.Count == 0)
            {
                continue;
            }

            var (missing, unexpected) = Diff(expectedNames, actualNames);
            comparisons.Add(new Comparison(
                FacetKind.Table, scope, "tablo", expectedNames, actualNames, missing, unexpected, false));
        }

        // Access is only meaningful for a table that was found at all; reporting it for a missing
        // table would count one loss twice.
        //
        // The POPULATION is every found table, not the mismatches. Scoring the mismatches as the
        // expected set inverts the row: six disagreements out of thirty-nine checks then read as
        // "six expected, none found, 0% recall", which is a far worse number than the one measured
        // and says nothing about the thirty-three that agreed.
        var checkable = expected
            .Where(t => actualByName.ContainsKey(t.Name))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        var accessMismatch = expected
            .Where(t => actualByName.ContainsKey(t.Name)
                && !string.Equals(Normalise(t.Access), Normalise(actualByName[t.Name].Access), StringComparison.Ordinal))
            .Select(t => $"{t.Name} bekleniyor {Normalise(t.Access)}, gelen {Normalise(actualByName[t.Name].Access)}")
            .Order(StringComparer.Ordinal)
            .ToList();

        if (checkable.Count > 0)
        {
            // Presence-only: an access claim is never made for a table nobody expected, so a
            // precision figure here would be a trivial 100% dressed up as a measurement.
            comparisons.Add(new Comparison(
                FacetKind.Table, EfScope.None, "erisim (R/W)", checkable, checkable, accessMismatch, [], true));
        }

        var kind = question.Expected.ColumnKind ?? ColumnKind.Write;
        var facet = kind == ColumnKind.Read ? FacetKind.ColumnRead : FacetKind.ColumnWrite;

        foreach (var scope in (EfScope[])[EfScope.Inside, EfScope.Outside])
        {
            var expectedColumns = expected
                .Where(t => Scope(scopes, t.Name) == scope)
                .SelectMany(t => t.Columns.Select(c => $"{t.Name}.{c}"))
                .Order(StringComparer.Ordinal)
                .ToList();

            var actualColumns = actual
                .Where(t => Scope(scopes, t.Table) == scope)
                .SelectMany(t => t.Columns.Select(c => $"{t.Table}.{c.Name}"))
                .Order(StringComparer.Ordinal)
                .ToList();

            if (expectedColumns.Count == 0 && actualColumns.Count == 0)
            {
                continue;
            }

            var (missing, unexpected) = Diff(expectedColumns, actualColumns);
            comparisons.Add(new Comparison(
                facet, scope, "kolon", expectedColumns, actualColumns, missing, unexpected, false));
        }

        // Evidence is scored over WRITE columns only. A read column carries no mechanism at all, so
        // every one of them would land in "bulunamadi" and merely restate the recall row.
        if (kind == ColumnKind.Read)
        {
            return;
        }

        foreach (var table in expected)
        {
            var found = actualByName.GetValueOrDefault(table.Name);

            foreach (var column in table.Columns.Order(StringComparer.Ordinal))
            {
                var match = found?.Columns.FirstOrDefault(c =>
                    string.Equals(c.Name, column, StringComparison.Ordinal));

                evidence.Add(match is null
                    ? new EvidenceItem(table.Name, column, EvidenceOutcome.NotFound, string.Empty)
                    : new EvidenceItem(
                        table.Name,
                        column,
                        match.Confidence is ClaimConfidence.Direct or ClaimConfidence.RowLevel
                            ? EvidenceOutcome.ExpectedMechanism
                            : EvidenceOutcome.DifferentButValid,
                        string.Join("+", match.Mechanisms)));
            }
        }
    }

    private static void CompareRoots(EvalQuestion question, TraceAnswer answer, List<Comparison> comparisons)
    {
        if (question.Expected.Roots is not { } expected)
        {
            return;
        }

        Add(RootKind.Endpoint, "endpoint", expected.Endpoint);
        Add(RootKind.Consumer, "consumer", expected.Consumer);
        Add(RootKind.BackgroundService, "arka plan isi", expected.BackgroundJob);

        void Add(RootKind rootKind, string subject, IReadOnlyList<string>? names)
        {
            if (names is null)
            {
                return;
            }

            var actual = (answer.EntryPoints?.Groups ?? [])
                .Where(g => g.RootKind == rootKind)
                .SelectMany(g => g.Nodes.Select(n => n.DisplayName))
                .Order(StringComparer.Ordinal)
                .ToList();

            var wanted = names.Order(StringComparer.Ordinal).ToList();
            var (missing, unexpected) = Diff(wanted, actual);

            comparisons.Add(new Comparison(
                FacetKind.Root, EfScope.None, subject, wanted, actual, missing, unexpected, false));
        }
    }

    /// <summary>
    /// Two questions, both read off the same answer. <c>consumedBy</c> is the set of consumer nodes
    /// reached across a CONSUMES edge; <c>publishedBy</c> is the set of EVENTS the flow publishes,
    /// which is the form the questions ask in ("which events does the dispatcher publish?").
    /// </summary>
    private static void CompareEvents(CodeGraph graph, EvalQuestion question, TraceAnswer answer, List<Comparison> comparisons)
    {
        if (question.Expected.Events is not { } expected)
        {
            return;
        }

        if (expected.ConsumedBy is { } consumed)
        {
            var actual = answer.EventBridges
                .SelectMany(b => b.ConsumedBy)
                .Select(nodeId => graph.Find(nodeId)?.DisplayName ?? nodeId)
                .Order(StringComparer.Ordinal)
                .ToList();

            var wanted = consumed.Order(StringComparer.Ordinal).ToList();
            var (missing, unexpected) = Diff(wanted, actual);

            comparisons.Add(new Comparison(
                FacetKind.Event, EfScope.None, "tuketen", wanted, actual, missing, unexpected, false));
        }

        if (expected.PublishedBy is { } published)
        {
            var actual = answer.EventBridges
                .Where(b => b.PublishedBy.Count > 0)
                .Select(b => ShortEventName(b.DisplayName))
                .Order(StringComparer.Ordinal)
                .ToList();

            var wanted = published.Order(StringComparer.Ordinal).ToList();
            var (missing, unexpected) = Diff(wanted, actual);

            comparisons.Add(new Comparison(
                FacetKind.Event, EfScope.None, "yayinlanan", wanted, actual, missing, unexpected, false));
        }
    }

    private static void CompareExternalStores(EvalQuestion question, TraceAnswer answer, List<Comparison> comparisons)
    {
        if (question.Expected.ExternalStores is not { } expected)
        {
            return;
        }

        var actual = answer.Nodes
            .Where(n => n.Kind == NodeKind.ExternalCall)
            .Select(n => n.DisplayName)
            .Order(StringComparer.Ordinal)
            .ToList();

        var wanted = expected.Order(StringComparer.Ordinal).ToList();

        // An ExternalCall node is displayed as "HTTP -> HttpEmbeddingService", so the class name the
        // question names is matched as a suffix rather than as the whole label.
        var matchedActual = new List<string>();
        var missing = new List<string>();

        var remaining = new List<string>(actual);

        foreach (var name in wanted)
        {
            var hit = remaining.FirstOrDefault(a =>
                string.Equals(a, name, StringComparison.Ordinal)
                || a.EndsWith(name, StringComparison.Ordinal));

            if (hit is null)
            {
                missing.Add(name);
                continue;
            }

            remaining.Remove(hit);
            matchedActual.Add(hit);
        }

        comparisons.Add(new Comparison(
            FacetKind.ExternalStore,
            EfScope.None,
            "dis depo",
            wanted,
            actual,
            missing,
            [.. remaining.Order(StringComparer.Ordinal)],
            false));
    }

    private static void CompareLimitations(EvalQuestion question, TraceAnswer answer, List<Comparison> comparisons)
    {
        if (question.Expected.Limitations is not { } expected)
        {
            return;
        }

        var actual = answer.Limitations
            .Select(l => l.Code)
            .Order(StringComparer.Ordinal)
            .ToList();

        var wanted = expected.Order(StringComparer.Ordinal).ToList();

        var missing = wanted
            .Where(code => !actual.Contains(code, StringComparer.Ordinal))
            .ToList();

        comparisons.Add(new Comparison(
            FacetKind.Limitation, EfScope.None, "sinir kodu", wanted, actual, missing, [], true));
    }

    private static void CompareNodes(EvalQuestion question, TraceAnswer answer, List<Comparison> comparisons)
    {
        if (question.Expected.Nodes is not { } expected)
        {
            return;
        }

        var actual = answer.Nodes
            .Select(n => n.DisplayName)
            .Order(StringComparer.Ordinal)
            .ToList();

        var wanted = expected.Order(StringComparer.Ordinal).ToList();
        var (missing, unexpected) = Diff(wanted, actual);

        comparisons.Add(new Comparison(
            FacetKind.Node, EfScope.None, "dugum", wanted, actual, missing, unexpected, false));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Multiset difference. Counts matter: <c>ProductChangedConsumer.Consume</c> is two distinct
    /// nodes with one display name, and set semantics would silently accept one where two are owed.
    /// </summary>
    private static (IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected) Diff(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        var pool = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in actual)
        {
            pool[item] = pool.GetValueOrDefault(item) + 1;
        }

        var missing = new List<string>();

        foreach (var item in expected)
        {
            if (pool.TryGetValue(item, out var count) && count > 0)
            {
                pool[item] = count - 1;
                continue;
            }

            missing.Add(item);
        }

        var unexpected = pool
            .Where(kv => kv.Value > 0)
            .SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value))
            .Order(StringComparer.Ordinal)
            .ToList();

        return ([.. missing.Order(StringComparer.Ordinal)], unexpected);
    }

    private static IReadOnlyDictionary<string, EfScope> ClassifyTables(CodeGraph graph)
    {
        var tablesByEntity = graph.Edges
            .Where(e => e.Kind == EdgeKind.MapsTo
                && e.FromId.StartsWith(NodeId.EntityPrefix, StringComparison.Ordinal))
            .GroupBy(e => e.FromId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList(), StringComparer.Ordinal);

        var efIssued = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            if (edge.Kind is not (EdgeKind.Reads or EdgeKind.Writes)
                || !EfIssuedMechanisms.Contains(edge.Mechanism))
            {
                continue;
            }

            foreach (var table in TargetTables(edge, tablesByEntity))
            {
                efIssued.Add(table);
            }
        }

        return graph.Nodes
            .Where(n => n.Kind == NodeKind.Table)
            .ToDictionary(
                n => n.DisplayName,
                n => efIssued.Contains(n.Id) ? EfScope.Inside : EfScope.Outside,
                StringComparer.Ordinal);
    }

    private static IEnumerable<string> TargetTables(Edge edge, IReadOnlyDictionary<string, List<string>> tablesByEntity)
    {
        if (edge.ToId.StartsWith(NodeId.TablePrefix, StringComparison.Ordinal))
        {
            yield return edge.ToId;
            yield break;
        }

        if (edge.ToId.StartsWith(NodeId.ColumnPrefix, StringComparison.Ordinal))
        {
            var qualified = edge.ToId[NodeId.ColumnPrefix.Length..];
            yield return NodeId.ForTable(qualified[..qualified.LastIndexOf('.')]);
            yield break;
        }

        if (tablesByEntity.TryGetValue(edge.ToId, out var mapped))
        {
            foreach (var table in mapped)
            {
                yield return table;
            }
        }
    }

    /// <summary>A table the graph does not know is outside EF by definition: it is not in the model.</summary>
    private static EfScope Scope(IReadOnlyDictionary<string, EfScope> scopes, string table) =>
        scopes.GetValueOrDefault(table, EfScope.Outside);

    /// <summary>"RW" and "WR" are the same claim; the answer builder emits W first.</summary>
    private static string Normalise(string access) =>
        string.Concat(access.Where(char.IsLetter).Select(char.ToUpperInvariant).Order());

    private static string ShortEventName(string displayName)
    {
        var dot = displayName.LastIndexOf('.');
        return dot >= 0 ? displayName[(dot + 1)..] : displayName;
    }

    public static string Label(FacetKind kind) => kind switch
    {
        FacetKind.Table => "tablo",
        FacetKind.ColumnWrite => "kolon-yazma",
        FacetKind.ColumnRead => "kolon-okuma",
        FacetKind.Root => "kok",
        FacetKind.Event => "event",
        FacetKind.ExternalStore => "dis depo",
        FacetKind.Limitation => "sinir",
        _ => "dugum",
    };
}
