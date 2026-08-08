namespace FlowLens.Core.Ef;

/// <param name="ClrTypeName">Fully qualified name, from Roslyn.</param>
/// <param name="AssemblyName">Assembly that declares it, used to find the dll in the host output.</param>
/// <param name="Location">Roslyn's file:line, so a failure can point at the declaration.</param>
public sealed record DbContextDeclaration(string ClrTypeName, string AssemblyName, string Location);

/// <summary>
/// Everything FlowLens learns from one DbContext's EF Core model, as plain data.
/// <para>
/// <b>This is a process-boundary contract, not just a DTO.</b> These records are the only things
/// that leave <see cref="EfProbe"/>. No EF Core type escapes - not <c>IModel</c>, not
/// <c>IEntityType</c>, not a <see cref="Type"/> handle - and every member is a string, a bool or a
/// list of them, so the whole thing round-trips through JSON unchanged
/// (<see cref="EfModelContract"/>, verified by a test on every run).
/// </para>
/// <para>
/// That is deliberate: it is what keeps the out-of-process escape hatch cheap. See
/// <see cref="EfProbe"/> for the four steps, and L14 in docs/known-limitations.md for why the
/// hatch exists at all.
/// </para>
/// <para>
/// Keyed by fully qualified CLR type NAME throughout, never by symbol or <see cref="Type"/>.
/// Phase 2 §4.2 learned this the hard way: Roslyn symbol identity is per-compilation, so the
/// Roslyn side and the reflection side can only meet on a string - and a string is also the only
/// thing that would survive a process hop.
/// </para>
/// </summary>
/// <param name="ContextClrTypeName">Fully qualified name of the DbContext this came from.</param>
/// <param name="DefaultSchema">Result of <c>HasDefaultSchema</c>, used as a correctness check.</param>
public sealed record EfModelSnapshot(
    string ContextClrTypeName,
    string? DefaultSchema,
    IReadOnlyList<EfEntity> Entities);

/// <param name="ClrTypeName">
/// Fully qualified name of the entity's CLR type. This is the join key against Roslyn: an
/// <c>ITypeSymbol</c> rendered with <see cref="NodeId.TypeFormat"/> produces the same string.
/// </param>
/// <param name="TableName">
/// Null when the type has no table of its own - it is owned and shares its owner's table, or it
/// is mapped into a JSON column. Never guessed from the type name.
/// </param>
/// <param name="OwnerClrTypeName">Owning entity for owned types; null for aggregate roots.</param>
/// <param name="IsMappedToJson">
/// True for owned types folded into a single JSON column (Cart.Items). Their members are NOT
/// columns, so they carry <see cref="EfProperty.JsonPropertyName"/> instead of a column name.
/// </param>
public sealed record EfEntity(
    string ClrTypeName,
    string? Schema,
    string? TableName,
    string? OwnerClrTypeName,
    bool IsMappedToJson,
    IReadOnlyList<EfProperty> Properties)
{
    /// <summary>schema.table, or null when this type owns no table.</summary>
    public string? QualifiedTableName => TableName is null
        ? null
        : Schema is null ? TableName : $"{Schema}.{TableName}";
}

/// <param name="Name">CLR property name, or the shadow property's model name.</param>
/// <param name="ColumnName">Null for JSON-mapped members, which have no column of their own.</param>
/// <param name="DeclaringClrTypeName">
/// The type that actually declares this member. Differs from the owning entity for complex
/// properties: Product.Price is a Money, so price_amount is declared by Money but is a column of
/// catalog.products.
/// </param>
/// <param name="IsShadow">
/// True when EF invented the property and no C# member exists (xmin, order_id, the owned
/// collection's synthetic id). These get NO Column node - there is no source location to
/// attribute one to, and fabricating one would break the roadmap's filePath+line invariant.
/// </param>
/// <param name="ComplexPropertyName">
/// Set when this column is reached through a complex property rather than declared on the entity:
/// price_amount is Money.Amount, arrived at via Product.Price. Without it, an assignment to
/// <c>Product.Price</c> would map to nothing, because Price itself owns no column - only its
/// members do.
/// </param>
/// <param name="ComplexPropertyOwnerClrTypeName">The type declaring that complex property.</param>
public sealed record EfProperty(
    string Name,
    string? ColumnName,
    string? JsonPropertyName,
    string DeclaringClrTypeName,
    bool IsShadow,
    bool IsRowVersion,
    string? StoreType,
    string? ComplexPropertyName = null,
    string? ComplexPropertyOwnerClrTypeName = null);

/// <summary>A DbContext Roslyn found but reflection could not turn into a model.</summary>
/// <param name="Location">Roslyn's file:line for the context declaration, so the report can point at it.</param>
public sealed record EfModelFailure(string ContextClrTypeName, string Location, string Reason);

/// <param name="UnresolvedAssemblies">
/// Names the load context could not supply. Never harmless: EF swallows ReflectionTypeLoadException
/// internally and silently drops the types it could not load, so a dropped
/// IEntityTypeConfiguration becomes a silently missing table.
/// </param>
/// <param name="Warnings">
/// Things worth saying that do not invalidate the model - a stale build, partial type loads.
/// Whether any of this blocks is <see cref="EfPreflight"/>'s decision, not this record's.
/// </param>
public sealed record EfModelReadResult(
    IReadOnlyList<EfModelSnapshot> Snapshots,
    IReadOnlyList<EfModelFailure> Failures,
    IReadOnlyList<string> UnresolvedAssemblies,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed)
{
    public static EfModelReadResult Empty { get; } = new([], [], [], [], TimeSpan.Zero);
}
