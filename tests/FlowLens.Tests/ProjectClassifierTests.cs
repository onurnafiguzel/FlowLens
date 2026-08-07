using FlowLens.Core;

namespace FlowLens.Tests;

/// <summary>
/// Pure-function tests for the path -> (module, layer) mapping. No I/O, so these run in
/// milliseconds and stay useful even when no target solution is available.
/// </summary>
public sealed class ProjectClassifierTests
{
    [Theory]
    [InlineData(
        @"C:\repos\ModularCommerce\src\Modules\Ordering\ModularCommerce.Ordering.Api\ModularCommerce.Ordering.Api.csproj",
        "ModularCommerce.Ordering.Api", "Ordering", "Api")]
    [InlineData(
        @"C:\repos\ModularCommerce\src\Modules\Catalog\ModularCommerce.Catalog.Infrastructure\ModularCommerce.Catalog.Infrastructure.csproj",
        "ModularCommerce.Catalog.Infrastructure", "Catalog", "Infrastructure")]
    [InlineData(
        "/repos/ModularCommerce/src/Modules/Cart/ModularCommerce.Cart.Domain/ModularCommerce.Cart.Domain.csproj",
        "ModularCommerce.Cart.Domain", "Cart", "Domain")]
    [InlineData(
        "/repos/ModularCommerce/src/Modules/Payment/ModularCommerce.Payment.Contracts/ModularCommerce.Payment.Contracts.csproj",
        "ModularCommerce.Payment.Contracts", "Payment", "Contracts")]
    public void Module_projects_are_classified_from_the_path(
        string path, string name, string expectedModule, string expectedLayer)
    {
        var info = ProjectClassifier.Classify(path, name);

        Assert.Equal(expectedModule, info.Module);
        Assert.Equal(expectedLayer, info.Layer);
        Assert.False(info.IsTest);
    }

    [Fact]
    public void Shared_projects_map_to_the_shared_bucket()
    {
        var info = ProjectClassifier.Classify(
            @"C:\repos\ModularCommerce\src\Shared\ModularCommerce.Shared.Kernel\ModularCommerce.Shared.Kernel.csproj",
            "ModularCommerce.Shared.Kernel");

        Assert.Equal(ProjectClassifier.SharedModule, info.Module);
        // "Kernel" is not one of the five DDD layers, so no layer is claimed.
        Assert.Null(info.Layer);
    }

    [Fact]
    public void Bootstrapper_maps_to_host()
    {
        var info = ProjectClassifier.Classify(
            @"C:\repos\ModularCommerce\src\Bootstrapper\ModularCommerce.Host\ModularCommerce.Host.csproj",
            "ModularCommerce.Host");

        Assert.Equal(ProjectClassifier.HostModule, info.Module);
    }

    [Theory]
    [InlineData("ModularCommerce.Ordering.UnitTests", "Ordering")]
    [InlineData("ModularCommerce.Payment.IntegrationTests", "Payment")]
    [InlineData("ModularCommerce.Shared.IntegrationTests", "Shared")]
    public void Test_projects_take_their_module_from_the_project_name(string name, string expectedModule)
    {
        var info = ProjectClassifier.Classify($@"C:\repos\ModularCommerce\tests\{name}\{name}.csproj", name);

        Assert.True(info.IsTest);
        Assert.Equal(expectedModule, info.Module);
    }

    [Theory]
    [InlineData("ModularCommerce.ArchitectureTests")]
    [InlineData("ModularCommerce.TestKit")]
    public void Test_projects_without_a_module_segment_stay_unknown(string name)
    {
        var info = ProjectClassifier.Classify($@"C:\repos\ModularCommerce\tests\{name}\{name}.csproj", name);

        Assert.True(info.IsTest);
        // Better an explicit "(unknown)" than forcing these into someone else's bucket.
        Assert.Equal(ProjectClassifier.UnknownModule, info.Module);
    }

    [Fact]
    public void Unrecognised_layouts_are_reported_as_unknown_rather_than_guessed()
    {
        var info = ProjectClassifier.Classify(@"D:\scratch\Some.Random.Project\Some.Random.Project.csproj", "Some.Random.Project");

        Assert.Equal(ProjectClassifier.UnknownModule, info.Module);
        Assert.Null(info.Layer);
        Assert.False(info.IsTest);
    }
}
