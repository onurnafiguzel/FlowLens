using Microsoft.CodeAnalysis;

namespace FlowLens.Core;

/// <summary>
/// The single authority on node identity. Phase 3 writes these strings into graph.json's "id"
/// field, so the format is frozen here rather than reinvented at each call site - changing it
/// later invalidates every stored graph and every test that names a node.
/// </summary>
public static class NodeId
{
    /// <summary>
    /// Fully qualified, parameters included.
    /// <para>
    /// Parameters are NOT optional: ProductChangedConsumer declares two Consume overloads that
    /// differ only in their ConsumeContext&lt;T&gt; argument. Without parameters they would
    /// collapse into one node and the two CONSUMES edges would be indistinguishable.
    /// </para>
    /// </summary>
    public static readonly SymbolDisplayFormat MemberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>Fully qualified type names, for Event ids and display.</summary>
    public static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    public const string EndpointPrefix = "endpoint:";
    public const string EventPrefix = "event:";
    public const string EntityPrefix = "entity:";
    public const string TablePrefix = "table:";
    public const string ColumnPrefix = "column:";
    public const string ExternalPrefix = "external:";

    /// <summary>
    /// The one form of a method symbol used for identity anywhere in FlowLens.
    /// <para>
    /// Two normalisations, both load-bearing. <c>OriginalDefinition</c> collapses generic
    /// instantiations (Select&lt;A,B&gt;, Select&lt;C,D&gt;, ...) onto one node, without which the
    /// visited set never converges. <c>ReducedFrom</c> undoes extension-method reduction: at a
    /// call site <c>group.MapOrderEndpoints()</c> binds to the reduced form with no parameters,
    /// while <c>GetEnclosingSymbol</c> inside that method returns the unreduced static form that
    /// still has its <c>this</c> parameter. Left unnormalised the two never match, and every
    /// endpoint declared in an extension method loses its route prefix.
    /// </para>
    /// </summary>
    public static IMethodSymbol Canonical(IMethodSymbol symbol) =>
        (symbol.ReducedFrom ?? symbol).OriginalDefinition;

    public static string ForMethod(IMethodSymbol symbol) =>
        Canonical(symbol).ToDisplayString(MemberFormat);

    public static string ForType(ITypeSymbol symbol) =>
        symbol.OriginalDefinition.ToDisplayString(TypeFormat);

    /// <summary>
    /// Id for an endpoint. The route is the natural key: when one registration method is mounted
    /// under two prefixes it legitimately produces two endpoints, and two distinct routes give
    /// two distinct ids for free.
    /// </summary>
    public static string ForEndpoint(string httpMethod, string route) =>
        $"{EndpointPrefix}{httpMethod} {route}";

    /// <summary>
    /// Id for an integration event. The fully qualified name is mandatory here:
    /// Ordering.Domain.Orders.OrderPaid and Ordering.Contracts.IntegrationEvents.OrderPaid share
    /// a short name but are different types (survey §6.1).
    /// </summary>
    public static string ForEvent(ITypeSymbol eventType) =>
        $"{EventPrefix}{ForType(eventType)}";

    /// <summary>
    /// Id for a mapped entity. The fully qualified CLR type name is also the join key against EF
    /// Core's model, which is why <see cref="TypeFormat"/> must render exactly what
    /// <c>Type.FullName</c> produces - the Roslyn side and the reflection side can only meet on a
    /// string, since symbol identity is per-compilation.
    /// </summary>
    public static string ForEntity(string clrTypeName) => $"{EntityPrefix}{clrTypeName}";

    public static string ForEntity(ITypeSymbol entityType) => ForEntity(ForType(entityType));

    /// <summary>
    /// Id for a table. The schema is not optional: ModularCommerce really does have both
    /// catalog.outbox_messages and ordering.outbox_messages, so an unqualified id would collapse
    /// two different tables into one node.
    /// </summary>
    public static string ForTable(string schemaQualifiedTableName) =>
        $"{TablePrefix}{schemaQualifiedTableName}";

    public static string ForColumn(string schemaQualifiedTableName, string columnName) =>
        $"{ColumnPrefix}{schemaQualifiedTableName}.{columnName}";

    /// <summary>
    /// Id for a call leaving the process, keyed by the type that makes it. The destination URL is
    /// deliberately not part of the id: it comes from configuration at runtime, so any hostname
    /// here would be invented rather than observed.
    /// </summary>
    public static string ForExternalCall(string callerClrTypeName) =>
        $"{ExternalPrefix}{callerClrTypeName}";

    /// <summary>Short, human-facing label. Never used as an identity key.</summary>
    public static string DisplayName(IMethodSymbol symbol) =>
        $"{symbol.ContainingType?.Name}.{symbol.Name}";
}
