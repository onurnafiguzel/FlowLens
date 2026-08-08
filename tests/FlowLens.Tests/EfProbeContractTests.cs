using FlowLens.Core.Ef;

namespace FlowLens.Tests;

/// <summary>
/// Proves the snapshot contract can already cross a process boundary.
/// <para>
/// Nothing serialises the model today - EfProbe hands the records straight to EfModelIndex in the
/// same process. This runs anyway, on the REAL target's model rather than a hand-made sample, so
/// the claim in EfProbe's migration comment stays a verified fact instead of decaying into a
/// comment nobody has checked. If someone ever puts a non-serialisable member on the contract, this
/// fails on the next run rather than on the day the probe is moved out of process.
/// </para>
/// </summary>
public sealed class EfProbeContractTests
{
    [Fact]
    public void TheRealTargetsModelRoundTripsThroughJson()
    {
        var original = ReadOrderingModel();

        var json = EfModelContract.Serialize(original);
        var restored = EfModelContract.Deserialize(json);

        // Re-serialising is the comparison: records hold IReadOnlyList fields, which compare by
        // reference, so structural equality has to be checked on the wire form itself.
        Assert.Equal(json, EfModelContract.Serialize(restored));
    }

    [Fact]
    public void RoundTripPreservesTheFactsTheGraphIsBuiltFrom()
    {
        var restored = EfModelContract.Deserialize(
            EfModelContract.Serialize(ReadOrderingModel()));

        var snapshot = Assert.Single(restored);
        Assert.Equal("ordering", snapshot.DefaultSchema);

        var order = snapshot.Entities.Single(e =>
            e.ClrTypeName == "ModularCommerce.Ordering.Domain.Orders.Order");

        Assert.Equal("ordering.orders", order.QualifiedTableName);
        Assert.Contains(order.Properties, p => p.Name == "Status" && p.ColumnName is not null);

        // Owned types and shadow flags are what the analyzers key on; losing either in transit
        // would change the graph without changing the code.
        var line = snapshot.Entities.Single(e =>
            e.ClrTypeName == "ModularCommerce.Ordering.Domain.Orders.OrderLine");

        Assert.Equal("ModularCommerce.Ordering.Domain.Orders.Order", line.OwnerClrTypeName);
        Assert.Contains(line.Properties, p => p.IsShadow);
    }

    [Fact]
    public void RejectsAPayloadFromADifferentContractVersion()
    {
        var json = EfModelContract.Serialize(ReadOrderingModel())
            .Replace("\"version\":1", "\"version\":99", StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(() => EfModelContract.Deserialize(json));
        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<EfModelSnapshot> ReadOrderingModel()
    {
        var result = EfProbe.Read(
            TargetBuildPaths.HostAssembly,
            [new DbContextDeclaration(
                "ModularCommerce.Ordering.Infrastructure.Persistence.OrderingDbContext",
                "ModularCommerce.Ordering.Infrastructure",
                "test")]);

        Assert.Empty(result.Failures);
        Assert.NotEmpty(result.Snapshots);

        return result.Snapshots;
    }
}
