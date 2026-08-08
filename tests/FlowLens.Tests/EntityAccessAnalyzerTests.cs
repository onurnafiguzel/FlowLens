using FlowLens.Core;
using FlowLens.Core.Ef;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Tests;

/// <summary>
/// Pins every EF access shape the analyzer claims to handle, on synthetic code.
/// <para>
/// Deliberately broader than the target repository. ModularCommerce never calls
/// <c>context.Add(entity)</c> and never calls <c>Update</c>, so a suite that only ran against it
/// would report green for shapes that were never exercised. Phase 2 learned this the expensive way:
/// the dropped-prefix bug was invisible in the real repo and only a synthetic case caught it.
/// </para>
/// </summary>
public sealed class EntityAccessAnalyzerTests
{
    private const string Source = """
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.EntityFrameworkCore;

        namespace Shop;

        public sealed class Order
        {
            public int Id { get; set; }
            public string Status { get; set; } = "";
        }

        public sealed class Product
        {
            public int Id { get; set; }
            public int Stock { get; set; }
        }

        public sealed class ShopContext : DbContext
        {
            public DbSet<Order> Orders => Set<Order>();
            public DbSet<Product> Products => Set<Product>();
        }

        public sealed class Repo(ShopContext context)
        {
            public void AddViaDbSet(Order order) => context.Orders.Add(order);

            public void AddViaSetOfT(Product product) => context.Set<Product>().Add(product);

            public void AddViaContext(Order order) => context.Add(order);

            public Task<int> DeleteViaChain(int id, CancellationToken token) =>
                context.Orders.Where(o => o.Id == id).ExecuteDeleteAsync(token);

            public Product? Read(int id) =>
                context.Products.Where(p => p.Id == id).FirstOrDefault();

            public Task<int> SaveOnly(Product product, CancellationToken token) =>
                context.SaveChangesAsync(token);

            public Order Construct() => new Order();

            public void RemoveViaDbSet(Order order) => context.Orders.Remove(order);

            public void UpdateViaDbSet(Product product) => context.Products.Update(product);

            // Remove first on purpose: whichever statement comes first must not decide the flag.
            public void RemoveThenAdd(Order a, Order b)
            {
                context.Orders.Remove(a);
                context.Orders.Add(b);
            }

            public string Unrelated() => "nothing to do with the database";
        }
        """;

    [Theory]
    [InlineData("AddViaDbSet", "Shop.Order", EdgeMechanism.DbSetProperty)]
    [InlineData("AddViaSetOfT", "Shop.Product", EdgeMechanism.SetOfT)]
    [InlineData("AddViaContext", "Shop.Order", EdgeMechanism.DbSetProperty)]
    [InlineData("DeleteViaChain", "Shop.Order", EdgeMechanism.FluentChainHead)]
    public async Task ResolvesWritesWithTheRightMechanism(
        string method,
        string entity,
        EdgeMechanism mechanism)
    {
        var accesses = await AnalyzeAsync(method);

        var write = Assert.Single(accesses, a => a.Kind == EdgeKind.Writes && a.EntityClrTypeName == entity);
        Assert.Equal(mechanism, write.Mechanism);

        // Evidence has to name a place a human can open.
        Assert.Contains(":", write.Evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// The DbSet is two links up the fluent chain here, so anything that only inspects the
    /// immediate receiver finds LINQ's Where and reports no entity at all.
    /// </summary>
    [Fact]
    public async Task FindsTheDbSetAtTheHeadOfAFluentChain()
    {
        var accesses = await AnalyzeAsync("DeleteViaChain");

        Assert.Contains(accesses, a => a.Kind == EdgeKind.Writes && a.EntityClrTypeName == "Shop.Order");
    }

    [Fact]
    public async Task ReportsQueriesAsReadsRatherThanWrites()
    {
        var accesses = await AnalyzeAsync("Read");

        var read = Assert.Single(accesses);
        Assert.Equal(EdgeKind.Reads, read.Kind);
        Assert.Equal("Shop.Product", read.EntityClrTypeName);
    }

    /// <summary>
    /// The second-class rule. ProductRepository.UpdateAsync in the real target has this exact
    /// shape - a write with no EF mutation call anywhere in the body - so keying purely on
    /// Add/Update/Remove would miss it. The mechanism tag is what keeps it distinguishable.
    /// </summary>
    [Fact]
    public async Task InfersAWriteFromSaveChangesPlusAnEntityParameter()
    {
        var accesses = await AnalyzeAsync("SaveOnly");

        var write = Assert.Single(accesses, a => a.Kind == EdgeKind.Writes);
        Assert.Equal("Shop.Product", write.EntityClrTypeName);
        Assert.Equal(EdgeMechanism.SaveChangesWithEntityParameter, write.Mechanism);
    }

    [Fact]
    public async Task TreatsConstructionOfAMappedTypeAsSecondClassEvidence()
    {
        var accesses = await AnalyzeAsync("Construct");

        var write = Assert.Single(accesses, a => a.Kind == EdgeKind.Writes);
        Assert.Equal(EdgeMechanism.EntityConstruction, write.Mechanism);
    }

    [Fact]
    public async Task ReportsNothingForAMethodThatNeverTouchesTheContext()
    {
        Assert.Empty(await AnalyzeAsync("Unrelated"));
    }

    /// <summary>
    /// Which statements write the WHOLE row, which is what decides whether the column set comes
    /// from the assignments or from the table.
    /// <para>
    /// An INSERT lists every mapped column and Update marks every property modified, so both write
    /// columns no C# statement names. Remove and ExecuteDelete write none, and a query writes
    /// nothing at all - the flag has to separate them or reading a table would start claiming its
    /// columns, which is the bug §5.3 removed once already.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("AddViaDbSet", true)]
    [InlineData("AddViaSetOfT", true)]
    [InlineData("AddViaContext", true)]
    [InlineData("UpdateViaDbSet", true)]
    [InlineData("Construct", true)]
    [InlineData("RemoveViaDbSet", false)]
    [InlineData("DeleteViaChain", false)]
    [InlineData("Read", false)]
    public async Task MarksOnlyTheStatementsThatWriteEveryColumnOfTheRow(string method, bool wholeRow)
    {
        var accesses = await AnalyzeAsync(method);

        Assert.Equal(wholeRow, accesses.Any(a => a.WritesWholeRow));
    }

    /// <summary>
    /// The accesses list de-duplicates by (entity, kind, mechanism), so a Remove recorded before an
    /// Add on the same DbSet would swallow the Add - and with it the fact that a row is inserted.
    /// </summary>
    [Fact]
    public async Task AWholeRowWriteSurvivesAnEarlierPartialWriteOnTheSameDbSet()
    {
        var accesses = await AnalyzeAsync("RemoveThenAdd");

        var write = Assert.Single(accesses, a => a.Kind == EdgeKind.Writes);
        Assert.True(write.WritesWholeRow);
    }

    private static async Task<IReadOnlyList<EntityAccess>> AnalyzeAsync(string methodName)
    {
        using var workspace = SyntheticWorkspace.Create(Source);
        await workspace.AssertCompilesAsync();

        var (_, root, model) = await workspace.OpenAsync("Source.cs");

        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == methodName);

        var analyzer = new EntityAccessAnalyzer(BuildModelIndex());

        return analyzer.Analyze(method, model, AppContext.BaseDirectory).Accesses;
    }

    /// <summary>
    /// A hand-built model, so these tests need no compiled target and no database - the analyzer
    /// only ever consumes names.
    /// </summary>
    private static EfModelIndex BuildModelIndex() =>
        new([
            new EfModelSnapshot("Shop.ShopContext", "shop",
            [
                Entity("Shop.Order", "orders", ("Id", "Id"), ("Status", "Status")),
                Entity("Shop.Product", "products", ("Id", "Id"), ("Stock", "Stock")),
            ]),
        ]);

    private static EfEntity Entity(string clrType, string table, params (string Name, string Column)[] properties) =>
        new(clrType, "shop", table, null, false,
            [.. properties.Select(p => new EfProperty(p.Name, p.Column, null, clrType, false, false, null))]);
}
