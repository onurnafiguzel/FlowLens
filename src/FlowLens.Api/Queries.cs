using FlowLens.Core;
using FlowLens.Core.Answers;

namespace FlowLens.Api;

/// <summary>
/// The five questions, answered from a loaded graph. No Roslyn, no MSBuild, no database, no LLM -
/// dictionary lookups and a breadth-first walk over a few hundred nodes.
/// </summary>
public static class Queries
{
    /// <summary>
    /// Default depth. The longest chain measured in this target is 10 (checkout), so 20 is double
    /// the observed maximum and cuts nothing real. Same value the CLI uses: two surfaces, one
    /// behaviour.
    /// </summary>
    public const int DefaultMaxDepth = 20;

    /// <summary>
    /// Five times the measured maximum. The cap is not about compute - a walk over 415 nodes is
    /// microseconds - it is about response size.
    /// </summary>
    public const int MaxAllowedDepth = 50;

    /// <summary>
    /// Utility nodes are hidden by default. L8 kept them in the graph on purpose and left this
    /// lever for consumers; measured across all 25 endpoints and all 16 tables, hiding them costs
    /// zero tables, zero columns and zero entry points. Every answer still reports how many were
    /// hidden - thinned and absent must not look alike.
    /// </summary>
    public const bool DefaultIncludeUtility = false;

    /// <summary>
    /// Standing caveats that do not vary by question. Published once on /graph/stats and referenced
    /// by code from an answer, rather than copied into every response where they would be noise.
    /// </summary>
    public static IReadOnlyList<Limitation> KnownLimitations { get; } =
    [
        new("non-relational-store",
            "Iliskisel olmayan depolar ontolojide yok. ExternalCall yalniz HttpClient cagrilarini " +
            "taniyor; Redis'e yazan bir akis (RedisCartCache) node uretmez. known-limitations L17.",
            []),
        new("owned-navigation-reads",
            "Owned bir koleksiyonun okunmasi READS kenari uretmiyor: sozdiziminde DbSet yok ama EF " +
            "auto-include ettigi icin SQL'de tablo var. Bir tablo 'yalniz yaziliyor' gorunebilir. " +
            "known-limitations L19.",
            []),
        new("column-reads-not-modelled",
            "Column node'lari yalniz bir YAZMA onlari adlandirdiginda uretiliyor. 'Bu kolonu kim " +
            "okuyor?' sorusunun graph'ta karsiligi yok - bos liste bir cevap degil. " +
            "known-limitations L18-2.",
            []),
        new("raw-sql-out-of-scope",
            "Ham SQL ile erisilen tablolar goruntmez (SQL parse etmek kapsam disi). Etkilenen her " +
            "site build diagnostics'inde file:line ile duruyor ve ilgili akisin cevabinda " +
            "limitations olarak cikar. known-limitations L6.",
            []),
    ];

    public static EndpointListAnswer Endpoints(GraphSnapshot snapshot, string? module)
    {
        var endpoints = snapshot.Graph.Nodes
            .Where(n => n.Kind == NodeKind.Endpoint)
            .Where(n => module is null || string.Equals(n.Module, module, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Module, StringComparer.Ordinal)
            .ThenBy(n => n.DisplayName, StringComparer.Ordinal)
            .Select(n =>
            {
                // "POST /api/ordering/checkout" - split here so no caller has to parse it.
                var space = n.DisplayName.IndexOf(' ');

                return new EndpointAnswer(
                    n.Id,
                    space > 0 ? n.DisplayName[..space] : n.DisplayName,
                    space > 0 ? n.DisplayName[(space + 1)..] : string.Empty,
                    n.Module,
                    n.FilePath,
                    n.Line);
            })
            .ToList();

        return new EndpointListAnswer(endpoints.Count, endpoints, []);
    }

    /// <summary>
    /// Every table, with the access it gets ACROSS THE WHOLE GRAPH rather than along one flow -
    /// "is this table ever written?" is the question a catalogue answers.
    /// </summary>
    public static TableListAnswer Tables(GraphSnapshot snapshot)
    {
        var whole = new Subgraph(
            snapshot.Graph.Nodes,
            snapshot.Graph.Edges,
            new Dictionary<string, int>(StringComparer.Ordinal));

        var dataLayer = AnswerBuilder.DataLayer(whole);

        return new TableListAnswer(dataLayer.Tables.Count, dataLayer.Tables, []);
    }

    public static IResult Trace(
        GraphSnapshot snapshot,
        string? node,
        int? maxDepth,
        bool? includeUtility,
        TraversalDirection direction)
    {
        if (string.IsNullOrWhiteSpace(node))
        {
            return Results.Problem(
                title: "node parametresi zorunlu",
                detail: "Ornek: /trace?node=POST%20%2Fapi%2Fordering%2Fcheckout veya " +
                        "?node=table%3Aordering.orders",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var depth = maxDepth ?? DefaultMaxDepth;

        // Out of range is a 400, never a silent clamp: a caller that asked for 200 and got 50
        // without being told would believe it had seen everything.
        if (depth < 1 || depth > MaxAllowedDepth)
        {
            return Results.Problem(
                title: "maxDepth araligin disinda",
                detail: $"Istenen {depth}; gecerli aralik 1-{MaxAllowedDepth}. Bu hedefte olculen " +
                        "en uzun zincir 10, varsayilan 20.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    ["requested"] = depth,
                    ["max"] = MaxAllowedDepth,
                    ["default"] = DefaultMaxDepth,
                    ["longestMeasuredChain"] = 10,
                });
        }

        var startId = NodeResolver.Resolve(snapshot.Graph, node);

        if (startId is null)
        {
            return Results.Problem(
                title: "node bulunamadi",
                detail: $"\"{node}\" hicbir node ile eslesmiyor.",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>
                {
                    ["requested"] = node,
                    ["didYouMean"] = NodeResolver.NearMatches(snapshot.Graph, node),
                });
        }

        var query = new TraversalQuery(depth, includeUtility ?? DefaultIncludeUtility);

        var subgraph = direction == TraversalDirection.Forward
            ? snapshot.Graph.ForwardSubgraph(startId, query)
            : snapshot.Graph.BackwardSubgraph(startId, query);

        return Results.Ok(AnswerBuilder.Build(
            snapshot.Graph, subgraph, startId, direction, query, snapshot.Diagnostics));
    }

    public static GraphStatsAnswer Stats(GraphSnapshot? snapshot, GraphSource source)
    {
        if (snapshot is null)
        {
            return new GraphStatsAnswer(
                "unavailable", source.LoadError, null, 0,
                new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, int>(),
                0, 0, 0, [], KnownLimitations, null, null, source.ReloadCount, 0, 0,
                source.Path, source.AttemptedPaths);
        }

        var stats = snapshot.Document.Stats;

        return new GraphStatsAnswer(
            "ok",
            source.LoadError,
            snapshot.Document.Solution,
            snapshot.Document.Version,
            stats.NodesByType,
            stats.EdgesByType,
            stats.EdgesByMechanism,
            stats.RootCount,
            stats.AmbiguousNodes,
            stats.UtilityNodes,
            snapshot.Diagnostics,
            KnownLimitations,
            snapshot.FileWrittenAtUtc.ToString("O"),
            snapshot.LoadedAtUtc.ToString("O"),
            source.ReloadCount,
            snapshot.FileLength,
            snapshot.ApproximateHeapBytes,
            source.Path,
            source.AttemptedPaths);
    }
}
