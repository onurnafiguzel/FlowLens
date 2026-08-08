using FlowLens.Core.Ef;

namespace FlowLens.Tests;

/// <summary>
/// Exercises the assembly-load-context design against the real target.
/// <para>
/// These deliberately skip Roslyn. Loading the solution takes ~16s and would hide what is actually
/// under test here: whether the target's DbContext types can be instantiated in this process and
/// their EF model read without a database. Anchors are type and table names, never counts - the
/// measured numbers belong in docs, not in assertions that break every time the target grows.
/// </para>
/// </summary>
public sealed class EfProbeTests
{
    private const string OrderingContext =
        "ModularCommerce.Ordering.Infrastructure.Persistence.OrderingDbContext";

    private const string OrderingAssembly = "ModularCommerce.Ordering.Infrastructure";

    private const string OrderEntity = "ModularCommerce.Ordering.Domain.Orders.Order";

    private const string OrderLineEntity = "ModularCommerce.Ordering.Domain.Orders.OrderLine";

    private const string CatalogContext =
        "ModularCommerce.Catalog.Infrastructure.Persistence.CatalogDbContext";

    private const string CatalogAssembly = "ModularCommerce.Catalog.Infrastructure";

    private const string ProductEntity = "ModularCommerce.Catalog.Domain.Products.Product";

    [Fact]
    public void ReadsTableNamesFromTheModelWithoutADatabase()
    {
        var result = Read(new DbContextDeclaration(OrderingContext, OrderingAssembly, "test"));

        Assert.Empty(result.Failures);
        Assert.Empty(result.UnresolvedAssemblies);

        var snapshot = Assert.Single(result.Snapshots);
        Assert.Equal("ordering", snapshot.DefaultSchema);

        var order = Find(snapshot, OrderEntity);
        Assert.Equal("ordering.orders", order.QualifiedTableName);
    }

    /// <summary>
    /// Owned collections mapped to their own table are the case that a naming convention would get
    /// wrong: OrderLine has no DbSet and its table name exists only inside OwnsMany(...).ToTable().
    /// </summary>
    [Fact]
    public void ResolvesOwnedTypesMappedToTheirOwnTable()
    {
        var result = Read(new DbContextDeclaration(OrderingContext, OrderingAssembly, "test"));
        var snapshot = Assert.Single(result.Snapshots);

        var line = Find(snapshot, OrderLineEntity);

        Assert.Equal("ordering.order_lines", line.QualifiedTableName);
        Assert.Equal(OrderEntity, line.OwnerClrTypeName);
    }

    /// <summary>
    /// Complex properties are the other case name inference cannot reach: Product.Price is a Money,
    /// and its members land on catalog.products as price_amount / price_currency. The property is
    /// declared by Money, not by Product, which is exactly what the analyzer needs to know.
    /// </summary>
    [Fact]
    public void ResolvesComplexPropertyColumnsToTheOwningTable()
    {
        var result = Read(new DbContextDeclaration(CatalogContext, CatalogAssembly, "test"));
        var snapshot = Assert.Single(result.Snapshots);

        var product = Find(snapshot, ProductEntity);
        Assert.Equal("catalog.products", product.QualifiedTableName);

        var amount = product.Properties.Single(p =>
            p.ColumnName == "price_amount");

        Assert.Equal("Amount", amount.Name);
        Assert.EndsWith(".Money", amount.DeclaringClrTypeName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Shadow properties exist in the model but have no C# member, so they can carry no source
    /// location. The graph must be able to tell them apart or it would have to fabricate one.
    /// </summary>
    [Fact]
    public void MarksShadowPropertiesSoTheyGetNoColumnNode()
    {
        var result = Read(new DbContextDeclaration(OrderingContext, OrderingAssembly, "test"));
        var snapshot = Assert.Single(result.Snapshots);

        var line = Find(snapshot, OrderLineEntity);

        Assert.Contains(line.Properties, p => p.IsShadow);
        Assert.Contains(line.Properties, p => !p.IsShadow);
    }

    [Fact]
    public void VersionGateAcceptsTheTargetsEfVersion()
    {
        var result = EfVersionGate.Check(HostAssemblyPath);

        Assert.True(
            result.Passed,
            "version gate rejected the target: " +
            string.Join(" | ", result.Problems.Select(p => p.Problem)));

        // A gate that finds nothing to compare would pass vacuously.
        Assert.Contains(result.Comparisons, c => c.TargetVersion is not null);
    }

    private static EfModelReadResult Read(DbContextDeclaration declaration) =>
        EfProbe.Read(HostAssemblyPath, [declaration]);

    private static EfEntity Find(EfModelSnapshot snapshot, string clrTypeName) =>
        snapshot.Entities.SingleOrDefault(e => e.ClrTypeName == clrTypeName)
        ?? throw new InvalidOperationException(
            $"{clrTypeName} is not in {snapshot.ContextClrTypeName}'s model. Present: " +
            string.Join(", ", snapshot.Entities.Select(e => e.ClrTypeName)));

    private static string HostAssemblyPath => TargetBuildPaths.HostAssembly;
}
