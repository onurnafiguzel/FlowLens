using Microsoft.CodeAnalysis;

namespace FlowLens.Core;

/// <summary>
/// Node types Phase 2 produces. Entity/Table/Column/ExternalCall belong to Phase 3 and are
/// deliberately absent - the roadmap keeps this vocabulary small on purpose.
/// </summary>
public enum NodeKind
{
    Endpoint,
    Handler,
    Method,
    Repository,
    Event,
}

/// <summary>Edge types Phase 2 produces. READS/WRITES/MAPS_TO are Phase 3.</summary>
public enum EdgeKind
{
    Calls,
    Publishes,
    Consumes,
}

/// <param name="Id">Stable identity from <see cref="NodeId"/>.</param>
/// <param name="FilePath">Solution-relative path. Mandatory - attribution is built on it.</param>
/// <param name="Line">1-based. 0 only when the symbol comes from metadata (no source).</param>
/// <param name="Ambiguous">True when this node was reached through an interface with several implementations.</param>
/// <param name="Truncated">True when traversal stopped here because of the depth limit.</param>
public sealed record TraceNode(
    string Id,
    NodeKind Kind,
    string DisplayName,
    string Module,
    string FilePath,
    int Line,
    bool Ambiguous = false,
    bool Truncated = false,
    int Depth = 0)
{
    public string Location => Line > 0 ? $"{FilePath}:{Line}" : FilePath;
}

/// <param name="Evidence">Why this edge exists, when that is not obvious from the endpoints alone.</param>
public sealed record TraceEdge(
    string FromId,
    string ToId,
    EdgeKind Kind,
    string? Evidence = null,
    bool Ambiguous = false,
    bool BridgeResolved = true);

/// <summary>Classifies a symbol into a <see cref="NodeKind"/> using the conventions measured in the survey.</summary>
public static class NodeKindClassifier
{
    public static NodeKind Classify(IMethodSymbol symbol)
    {
        var typeName = symbol.ContainingType?.Name ?? string.Empty;
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;

        // Data access. Both halves of the CQRS-lite split count: writes go through
        // I<X>Repository, reads through I<X>Queries (survey §7).
        if (typeName.EndsWith("Repository", StringComparison.Ordinal)
            || typeName.EndsWith("Queries", StringComparison.Ordinal))
        {
            return NodeKind.Repository;
        }

        // Use-case handlers. The name alone is not enough: GlobalExceptionHandler in
        // Shared.Infrastructure matches the suffix but is an IExceptionHandler, not a use case
        // (survey §3.2). Requiring an .Application namespace excludes it.
        if (typeName.EndsWith("Handler", StringComparison.Ordinal)
            && ns.Contains(".Application", StringComparison.Ordinal))
        {
            return NodeKind.Handler;
        }

        return NodeKind.Method;
    }
}
