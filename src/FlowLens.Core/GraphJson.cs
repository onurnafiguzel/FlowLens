using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowLens.Core;

/// <summary>Thrown when a graph violates an invariant the roadmap makes mandatory.</summary>
public sealed class InvalidGraphException(string message) : Exception(message);

/// <param name="NodesByType">Node kind -> count.</param>
/// <param name="EdgesByType">Edge kind -> count.</param>
/// <param name="EdgesByMechanism">
/// How each edge was derived. Split out because a WRITES edge inferred from a bare SaveChanges is
/// not the same claim as one read off context.Orders.Add, and a single count would hide that.
/// </param>
/// <param name="ElapsedMs">
/// Build wall-clock. Deliberately NOT serialised: an artifact that embeds its own build duration
/// can never be byte-identical across two builds of unchanged source, and that reproducibility is
/// what makes graph.json's diff readable and Phase 5's output deterministic. The number is still
/// reported on the console, where it belongs - it says something about the machine, not about the
/// graph. Consumers that want freshness read the file's timestamp.
/// </param>
public sealed record GraphStats(
    IReadOnlyDictionary<string, int> NodesByType,
    IReadOnlyDictionary<string, int> EdgesByType,
    IReadOnlyDictionary<string, int> EdgesByMechanism,
    int AmbiguousNodes,
    int UtilityNodes,
    int RootCount,
    [property: JsonIgnore] long ElapsedMs);

/// <param name="Diagnostics">
/// Everything the build knew it could not represent: raw SQL sites, properties with no column,
/// contexts that failed to load. Present in the file on purpose - a consumer must be able to tell
/// "this flow touches no tables" from "we could not look".
/// </param>
public sealed record GraphDocument(
    int Version,
    string Solution,
    GraphStats Stats,
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Edge> Edges,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Reads and writes graph.json, and enforces the invariants on the way out.
/// <para>
/// Validation lives here rather than in a test because the roadmap makes filePath and line
/// mandatory for every node: attribution is the whole point, and an answer nobody can check
/// against the source is worth less than no answer. A graph that cannot be attributed is not
/// written at all.
/// </para>
/// </summary>
public static class GraphJson
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// Only nulls are omitted - never default values.
    /// <para>
    /// This used to be <c>WhenWritingDefault</c>, which silently dropped every field whose value
    /// happened to be the enum's zero: 25 Endpoint nodes shipped with no <c>kind</c>, and all 512
    /// CALLS edges with no <c>kind</c> either. Every consumer would then have had to know that a
    /// missing field means "Endpoint" or "Calls" - an unwritten rule, and the kind of thing that
    /// silently produces a wrong reading rather than an error. Kind is the field the whole schema
    /// turns on, so it is always present.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Write(string path, GraphDocument document)
    {
        Validate(document.Nodes, document.Edges);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(Canonical(document), Options));
    }

    /// <summary>
    /// The same graph always serialises to the same bytes, whatever order it was discovered in.
    /// <para>
    /// Measured: two builds of an unchanged target produced set-identical files - 415 nodes and 966
    /// edges, zero differences - in which 8 nodes and 40 edges had simply moved. The cause is
    /// upstream of this file (<c>SymbolFinder.FindImplementationsAsync</c> does not promise an
    /// order), so the fix belongs here rather than in every producer.
    /// </para>
    /// <para>
    /// Why it matters more than tidiness: a one-field change showed up as a 216-line diff, 214 of
    /// them noise. Phase 3's four real bugs were found by READING graph.json, and that is not
    /// possible against that much churn. Phase 5 also requires byte-identical output from an
    /// unchanged graph, which a random order can never satisfy.
    /// </para>
    /// <para>
    /// The keys are total. Node ids are unique by construction - they are the builder's dictionary
    /// key. Edges sort on every field they carry, so two edges that tie on the key are identical
    /// records and their relative order cannot change a byte.
    /// </para>
    /// </summary>
    public static GraphDocument Canonical(GraphDocument document) => document with
    {
        Nodes = [.. document.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal)],
        Edges =
        [
            .. document.Edges
                .OrderBy(e => e.FromId, StringComparer.Ordinal)
                .ThenBy(e => e.ToId, StringComparer.Ordinal)
                .ThenBy(e => e.Kind)
                .ThenBy(e => e.Mechanism)
                .ThenBy(e => e.Evidence ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.Ambiguous),
        ],
        Diagnostics = [.. document.Diagnostics.OrderBy(d => d, StringComparer.Ordinal)],
    };

    public static GraphDocument Read(string path)
    {
        var document = JsonSerializer.Deserialize<GraphDocument>(File.ReadAllText(path), Options)
            ?? throw new InvalidGraphException($"{path} did not deserialise into a graph.");

        if (document.Version != SchemaVersion)
        {
            throw new InvalidGraphException(
                $"{path} is schema version {document.Version}; this build reads {SchemaVersion}.");
        }

        return document;
    }

    public static CodeGraph ToGraph(GraphDocument document) => new(document.Nodes, document.Edges);

    /// <summary>
    /// Fails the build rather than shipping an unattributable graph. Also checks that edges point
    /// at nodes that exist, since a dangling edge silently truncates every traversal through it.
    /// </summary>
    public static void Validate(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        var problems = new List<string>();

        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.FilePath) || node.FilePath == SourceLocation.NoSource)
            {
                problems.Add($"{node.Id} has no filePath");
            }

            if (node.Line <= 0)
            {
                problems.Add($"{node.Id} has line {node.Line}");
            }

            // A root cannot be plumbing. The utility tag is structural (declaring module is
            // Shared), and Shared holds both real helpers and one real entry point -
            // MigrateAndSeedHostedService, a BackgroundService that seeds two tables. Tagged as
            // utility it vanished from four backward answers for any consumer that thins utility
            // nodes, taking a "who writes this table?" answer with it. GraphBuilder clears the tag
            // on every root; this is the check that keeps it cleared, so the next root declared in
            // Shared cannot repeat it silently.
            if (node.RootKind != RootKind.None && node.Utility)
            {
                problems.Add($"{node.Id} is a {node.RootKind} root but is marked utility");
            }
        }

        var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            if (!ids.Contains(edge.FromId))
            {
                problems.Add($"edge {edge.Kind} starts at unknown node {edge.FromId}");
            }

            if (!ids.Contains(edge.ToId))
            {
                problems.Add($"edge {edge.Kind} ends at unknown node {edge.ToId}");
            }
        }

        if (problems.Count == 0)
        {
            return;
        }

        throw new InvalidGraphException(
            $"graph failed validation with {problems.Count} problem(s):{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", problems.Take(20)));
    }

    public static GraphStats BuildStats(
        IReadOnlyList<Node> nodes,
        IReadOnlyList<Edge> edges,
        int rootCount,
        TimeSpan elapsed) =>
        new(
            nodes.GroupBy(n => n.Kind.ToString())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            edges.GroupBy(e => e.Kind.ToString())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            edges.Where(e => e.Mechanism != EdgeMechanism.None)
                .GroupBy(e => e.Mechanism.ToString())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            nodes.Count(n => n.Ambiguous),
            nodes.Count(n => n.Utility),
            rootCount,
            (long)elapsed.TotalMilliseconds);
}
