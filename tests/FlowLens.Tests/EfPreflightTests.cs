using FlowLens.Core.Ef;

namespace FlowLens.Tests;

/// <summary>
/// The blocking paths, driven with synthetic inputs.
/// <para>
/// These matter more than the happy path and are far harder to reach by accident: a version skew
/// or a half-loaded assembly is exactly the situation where nobody is watching closely. Each test
/// asserts on the CONTENT of the message, not just that something was refused - a hard stop whose
/// message does not say what to do is only marginally better than a silent wrong answer.
/// </para>
/// </summary>
public sealed class EfPreflightTests
{
    private const string EfPackage = "Microsoft.EntityFrameworkCore.Relational";

    [Fact]
    public void BlocksWhenTheTargetHasNotBeenBuilt()
    {
        var report = EfPreflight.BeforeRead(
            new TargetBuildOutput(null, "ModularCommerce.Host", false, "cikti bulunamadi"),
            @"C:\repo\Target.sln");

        Assert.True(report.Blocked);

        // The exact command to run, not a description of it.
        Assert.Contains("dotnet build", report.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\repo\Target.sln", report.Message, StringComparison.Ordinal);
        Assert.Contains("Cozum", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The MSBL001-shaped failure: the runtime binds happily and breaks much later somewhere else,
    /// so the message has to name both versions AND explain why it is not self-evident.
    /// </summary>
    [Fact]
    public void BlocksOnVersionSkewAndNamesBothVersionsAndTheFix()
    {
        using var target = FakeTarget.WithEfVersion("99.1.2");

        var report = EfPreflight.BeforeRead(
            target.BuildOutput,
            @"C:\repo\Target.sln",
            _ => new Version(10, 0, 9));

        Assert.True(report.Blocked);

        Assert.Contains("99.1.2", report.Message, StringComparison.Ordinal);          // target
        Assert.Contains("10.0.9", report.Message, StringComparison.Ordinal);          // FlowLens
        Assert.Contains(EfPackage, report.Message, StringComparison.Ordinal);         // what to edit
        Assert.Contains("FlowLens.Core.csproj", report.Message, StringComparison.Ordinal);
        Assert.Contains("Neden burada duruyoruz", report.Message, StringComparison.Ordinal);
        Assert.Contains("TPA", report.Message, StringComparison.Ordinal);
        Assert.Contains("L14", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PassesWhenFlowLensIsNewerThanTheTarget()
    {
        using var target = FakeTarget.WithEfVersion("10.0.4");

        var report = EfPreflight.BeforeRead(
            target.BuildOutput, @"C:\repo\Target.sln", _ => new Version(10, 0, 9));

        Assert.False(report.Blocked);
    }

    /// <summary>
    /// The dangerous case: a well-formed graph that is quietly missing one module's tables. Before
    /// this existed, seven contexts out of eight exited 0 and wrote the file.
    /// </summary>
    [Fact]
    public void BlocksWhenAContextRoslynFoundCouldNotBeRead()
    {
        var report = EfPreflight.AfterRead(
            new EfModelReadResult([Snapshot("A")], [], [], [], TimeSpan.Zero),
            [Declared("A"), Declared("B")]);

        Assert.True(report.Blocked);
        Assert.Contains("2 DbContext", report.Message, StringComparison.Ordinal);
        Assert.Contains("1 DbContext", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlocksWhenAContextThrewAndPointsAtItsDeclaration()
    {
        var report = EfPreflight.AfterRead(
            new EfModelReadResult(
                [],
                [new EfModelFailure("Shop.ShopContext", "src/Shop/ShopContext.cs:7", "boom")],
                [], [], TimeSpan.Zero),
            [Declared("Shop.ShopContext")]);

        Assert.True(report.Blocked);
        Assert.Contains("src/Shop/ShopContext.cs:7", report.Message, StringComparison.Ordinal);
        Assert.Contains("boom", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unresolved assembly is never harmless: EF swallows ReflectionTypeLoadException and drops
    /// the types it could not load, so a missing IEntityTypeConfiguration is a missing table with
    /// no error anywhere.
    /// </summary>
    [Fact]
    public void BlocksOnAnUnresolvedAssembly()
    {
        var report = EfPreflight.AfterRead(
            new EfModelReadResult([Snapshot("A")], [], ["Microsoft.Extensions.Hosting.Abstractions"], [], TimeSpan.Zero),
            [Declared("A")]);

        Assert.True(report.Blocked);
        Assert.Contains("Hosting.Abstractions", report.Message, StringComparison.Ordinal);
        Assert.Contains("FrameworkReference", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlocksWhenAContextProducedNoTables()
    {
        var empty = new EfModelSnapshot("Shop.ShopContext", "shop",
            [new EfEntity("Shop.Thing", null, null, null, false, [])]);

        var report = EfPreflight.AfterRead(
            new EfModelReadResult([empty], [], [], [], TimeSpan.Zero),
            [Declared("Shop.ShopContext")]);

        Assert.True(report.Blocked);
        Assert.Contains("ShopContext", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PassesWhenEveryDeclaredContextProducedTables()
    {
        var report = EfPreflight.AfterRead(
            new EfModelReadResult([Snapshot("A"), Snapshot("B")], [], [], [], TimeSpan.Zero),
            [Declared("A"), Declared("B")]);

        Assert.False(report.Blocked);
        Assert.Equal(string.Empty, report.Message);
    }

    private static EfModelSnapshot Snapshot(string context) =>
        new(context, "s", [new EfEntity($"{context}.Thing", "s", "things", null, false, [])]);

    private static DbContextDeclaration Declared(string context) =>
        new(context, "Asm", "file.cs:1");

    /// <summary>
    /// A directory that looks enough like a built target for the version gate: a host assembly path
    /// and the deps.json beside it, which is where the target's EF version actually comes from.
    /// </summary>
    private sealed class FakeTarget : IDisposable
    {
        private readonly string _directory;

        private FakeTarget(string directory, TargetBuildOutput buildOutput)
        {
            _directory = directory;
            BuildOutput = buildOutput;
        }

        public TargetBuildOutput BuildOutput { get; }

        public static FakeTarget WithEfVersion(string version)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"flowlens-preflight-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            var hostPath = Path.Combine(directory, "Fake.Host.dll");
            File.WriteAllText(hostPath, string.Empty);

            File.WriteAllText(
                Path.Combine(directory, "Fake.Host.deps.json"),
                $$"""
                  { "libraries": { "{{EfPackage}}/{{version}}": { "type": "package" } } }
                  """);

            return new FakeTarget(directory, new TargetBuildOutput(hostPath, "Fake.Host", false, null));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
