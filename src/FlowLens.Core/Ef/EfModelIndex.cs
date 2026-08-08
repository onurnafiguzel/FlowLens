namespace FlowLens.Core.Ef;

/// <param name="Entity">The mapped entity.</param>
/// <param name="ContextClrTypeName">Which DbContext's model it came from - carried as edge evidence.</param>
public sealed record EntityMapping(EfEntity Entity, string ContextClrTypeName);

/// <param name="QualifiedTableName">schema.table the column belongs to.</param>
/// <param name="Property">The EF property, including its column name.</param>
public sealed record ColumnMapping(
    string QualifiedTableName,
    string EntityClrTypeName,
    EfProperty Property);

/// <summary>
/// Name-keyed lookup over every loaded EF model, so the Roslyn analyzers can ask "is this type
/// mapped, and to what?" without ever touching an EF type.
/// <para>
/// Everything is keyed by fully qualified CLR type name rather than by symbol. That is not a
/// convenience: Phase 2 §4.2 established that Roslyn symbol identity is per-compilation, so the
/// same entity observed from its Domain project and from its Infrastructure project is two unequal
/// symbols. A name is the only key both halves agree on - and it is also the only key the
/// reflection side can produce at all.
/// </para>
/// </summary>
public sealed class EfModelIndex
{
    private readonly Dictionary<string, List<EntityMapping>> _entitiesByClrName;
    private readonly Dictionary<(string DeclaringType, string Property), List<ColumnMapping>> _columns;

    public EfModelIndex(IReadOnlyList<EfModelSnapshot> snapshots)
    {
        Snapshots = snapshots;

        _entitiesByClrName = new Dictionary<string, List<EntityMapping>>(StringComparer.Ordinal);
        _columns = [];

        foreach (var snapshot in snapshots)
        {
            foreach (var entity in snapshot.Entities)
            {
                if (!_entitiesByClrName.TryGetValue(entity.ClrTypeName, out var mappings))
                {
                    mappings = [];
                    _entitiesByClrName[entity.ClrTypeName] = mappings;
                }

                mappings.Add(new EntityMapping(entity, snapshot.ContextClrTypeName));

                if (entity.QualifiedTableName is not { } table)
                {
                    continue;
                }

                foreach (var property in entity.Properties)
                {
                    if (property.ColumnName is null)
                    {
                        continue;
                    }

                    var mapping = new ColumnMapping(table, entity.ClrTypeName, property);

                    Index((property.DeclaringClrTypeName, property.Name), mapping);

                    // Second key for complex members, so assigning the whole value object -
                    // product.Price = money - resolves to every column it backs, not to nothing.
                    if (property.ComplexPropertyName is { } complexName
                        && property.ComplexPropertyOwnerClrTypeName is { } complexOwner)
                    {
                        Index((complexOwner, complexName), mapping);
                    }
                }
            }
        }

        void Index((string, string) key, ColumnMapping mapping)
        {
            if (!_columns.TryGetValue(key, out var columns))
            {
                columns = [];
                _columns[key] = columns;
            }

            columns.Add(mapping);
        }
    }

    public IReadOnlyList<EfModelSnapshot> Snapshots { get; }

    public static EfModelIndex Empty { get; } = new([]);

    public bool IsMappedEntity(string clrTypeName) => _entitiesByClrName.ContainsKey(clrTypeName);

    /// <summary>
    /// Whether touching this type may be attributed to a table on identity alone.
    /// <para>
    /// Shared value objects are the exception, and they matter. <c>Shared.Kernel.Money</c> is a
    /// complex property in Catalog but an OwnsOne entity in Ordering, so it appears in the model as
    /// an owned type of <c>OrderLine</c>. Without this test, any module constructing a Money -
    /// Catalog creating a product, Payment recording an amount - would get an edge to
    /// <c>ordering.order_lines</c>, which is confidently wrong in exactly the way the roadmap warns
    /// about. Measured, not hypothetical: it produced that edge before this existed.
    /// </para>
    /// <para>
    /// An owned type declared alongside its owner (OrderLine in Ordering, PaymentAttempt in
    /// Payment) is unaffected - its table is unambiguous.
    /// </para>
    /// </summary>
    public bool IsAttributableEntity(string clrTypeName) =>
        FindEntity(clrTypeName).Any(m =>
            m.Entity.OwnerClrTypeName is null
            || ModuleOf(m.Entity.ClrTypeName) == ModuleOf(m.Entity.OwnerClrTypeName));

    /// <summary>Module segment of a fully qualified name: ModularCommerce.Ordering.Domain.Order -> Ordering.</summary>
    internal static string ModuleOf(string clrTypeName)
    {
        var segments = clrTypeName.Split('.');
        return segments.Length >= 2 ? segments[1] : clrTypeName;
    }

    /// <summary>
    /// Every model that maps this type. Normally one. More than one is possible in principle - two
    /// contexts can map the same CLR type - and is returned rather than silently resolved, so the
    /// caller can mark the resulting edges ambiguous instead of picking a winner at random.
    /// </summary>
    public IReadOnlyList<EntityMapping> FindEntity(string clrTypeName) =>
        _entitiesByClrName.TryGetValue(clrTypeName, out var mappings) ? mappings : [];

    /// <summary>
    /// Columns a given CLR member maps to, keyed by the type that DECLARES the member.
    /// <para>
    /// The declaring type is often not the entity. Product.Price is a Money, so assigning to
    /// Amount is an assignment on Money - and that same Money member backs catalog.products
    /// price_amount, payment.payments Amount, and ordering.order_lines UnitPrice. All matches are
    /// returned; the caller decides how to report a member that reaches several columns.
    /// </para>
    /// </summary>
    public IReadOnlyList<ColumnMapping> FindColumns(string declaringClrTypeName, string propertyName) =>
        _columns.TryGetValue((declaringClrTypeName, propertyName), out var columns) ? columns : [];

    /// <summary>Every distinct schema-qualified table across all loaded models.</summary>
    public IEnumerable<(string QualifiedTableName, EntityMapping Owner)> Tables() =>
        _entitiesByClrName.Values
            .SelectMany(m => m)
            .Where(m => m.Entity.QualifiedTableName is not null)
            .GroupBy(m => m.Entity.QualifiedTableName!, StringComparer.Ordinal)
            .Select(g => (g.Key, g.First()));
}
