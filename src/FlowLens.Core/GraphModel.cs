using Microsoft.CodeAnalysis;

namespace FlowLens.Core;

/// <summary>
/// The node vocabulary from the roadmap, complete as of Phase 3.
/// <para>
/// Deliberately small. Phase 2 shipped the first five; Phase 3 adds the data-layer four. Anything
/// beyond this list is an ontology change and needs asking about first, because every consumer -
/// traversal, graph.json, the eventual bots - is written against exactly these.
/// </para>
/// </summary>
public enum NodeKind
{
    Endpoint,
    Handler,
    Method,
    Repository,
    Event,
    Entity,
    Table,
    Column,
    ExternalCall,
}

public enum EdgeKind
{
    Calls,
    Publishes,
    Consumes,
    Reads,
    Writes,
    MapsTo,
}

/// <summary>
/// How an edge was derived. Phase 2's validation found that 5 of 13 interface resolutions produced
/// the right answer through the wrong mechanism, and no field on the edge could tell you which.
/// This is that field.
/// <para>
/// The distinction that matters is first-class versus second-class. A first-class mechanism reads
/// the fact directly out of the code or the EF model. A second-class one infers it from
/// circumstantial evidence and can be wrong in ways a correct-looking result will not reveal - so
/// they are counted separately in reports and can be measured separately by the Phase 5 eval set.
/// </para>
/// </summary>
public enum EdgeMechanism
{
    /// <summary>CALLS/PUBLISHES/CONSUMES, where the edge kind already says everything.</summary>
    None,

    /// <summary>context.Orders.Add(x) - receiver is a member typed DbSet&lt;T&gt;.</summary>
    DbSetProperty,

    /// <summary>context.Set&lt;T&gt;().Add(x).</summary>
    SetOfT,

    /// <summary>.Where(...).ExecuteDeleteAsync() - the DbSet sits at the head of a fluent chain.</summary>
    FluentChainHead,

    /// <summary>ExecuteUpdateAsync(s =&gt; s.SetProperty(r =&gt; r.Expires, ...)) - column level.</summary>
    ExecuteUpdateSetProperty,

    /// <summary>Status = next; on a property of a mapped entity.</summary>
    PropertyAssignment,

    /// <summary>_statusHistory.Add(new OrderStatusChange(...)) - a row in the owned type's table.</summary>
    OwnedCollectionAdd,

    /// <summary>Assignment inside a mapped entity's own constructor.</summary>
    EntityConstructorAssignment,

    /// <summary>
    /// SECOND CLASS. The method calls SaveChanges and has a parameter or local whose type is an
    /// entity of that context's model, but performs no EF mutation call. Structural rather than
    /// name-based, but circumstantial: it infers the write from the change tracker's behaviour.
    /// </summary>
    SaveChangesWithEntityParameter,

    /// <summary>
    /// SECOND CLASS. new T(...) where T is in a loaded model. Construction is not persistence; this
    /// only establishes that the flow reaches the entity. Kept because it is the sole route to
    /// entities written exclusively through raw SQL.
    /// </summary>
    EntityConstruction,

    /// <summary>
    /// A SaveChanges interceptor attached to this context writes the entity, even though the
    /// calling code never mentions it. Sound but indirect: which contexts an interceptor is
    /// attached to is a DI fact, so the link is made through the EF model instead - see
    /// <c>DataLayerOverlay</c> for the assumption that carries it.
    /// </summary>
    SaveChangesInterceptor,

    /// <summary>MAPS_TO, sourced from EF Core's IModel.</summary>
    EfModelMapping,

    /// <summary>An HttpClient call leaving the process.</summary>
    HttpClientInvocation,

    /// <summary>PUBLISHES, resolved through the module's domain-to-integration event registry.</summary>
    DomainEventRegistry,

    /// <summary>CONSUMES, from an IConsumer&lt;T&gt; implementation.</summary>
    ConsumerRegistration,
}

/// <param name="Id">Stable identity from <see cref="NodeId"/>. Written verbatim into graph.json.</param>
/// <param name="FilePath">Solution-relative path. Mandatory - attribution is built on it.</param>
/// <param name="Line">1-based and mandatory. A node that cannot supply one must not be created.</param>
/// <param name="Ambiguous">Reached through an interface with several implementations.</param>
/// <param name="Truncated">Traversal stopped here because of the depth limit.</param>
/// <param name="Utility">
/// Shared-kernel plumbing such as Result.Success or Error.Validation. Real calls, kept in the
/// graph, but they carry no impact-analysis information. Tagged rather than filtered: dropping
/// them would be an irreversible loss decided on a hunch, whereas a tag lets each consumer choose.
/// The rule is structural - the declaring project's module is Shared - not a name heuristic.
/// </param>
/// <param name="Depth">Depth of first discovery from whichever root found it.</param>
public sealed record Node(
    string Id,
    NodeKind Kind,
    string DisplayName,
    string Module,
    string FilePath,
    int Line,
    bool Ambiguous = false,
    bool Truncated = false,
    bool Utility = false,
    int Depth = 0)
{
    public string Location => Line > 0 ? $"{FilePath}:{Line}" : FilePath;
}

/// <param name="Evidence">
/// Why this edge exists, in file:line terms, when that is not obvious from its endpoints. This is
/// what makes a claim checkable by hand rather than merely plausible.
/// </param>
public sealed record Edge(
    string FromId,
    string ToId,
    EdgeKind Kind,
    string? Evidence = null,
    bool Ambiguous = false,
    EdgeMechanism Mechanism = EdgeMechanism.None);

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
