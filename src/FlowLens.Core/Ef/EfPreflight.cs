using Microsoft.CodeAnalysis;

namespace FlowLens.Core.Ef;

/// <param name="Blocked">True when the caller must stop rather than produce a graph.</param>
/// <param name="Message">Operator-facing text, already formatted. Empty when not blocked.</param>
public sealed record EfPreflightReport(bool Blocked, string Message)
{
    public static readonly EfPreflightReport Clear = new(false, string.Empty);
}

/// <summary>Thrown when the EF model cannot be trusted, so no graph is produced at all.</summary>
public sealed class EfPreflightException(string message) : Exception(message);

/// <summary>
/// Decides whether the EF model can be trusted, and says so in terms an operator can act on.
///
/// <para>
/// <b>Nothing here degrades gracefully, on purpose.</b> Every condition below removes tables from
/// the graph without removing the graph, and a graph that is missing a module's tables answers
/// "this flow touches nothing" with total confidence. Continuing with a partial model would ship
/// exactly the failure this project exists to prevent, so each condition is a hard stop.
/// </para>
///
/// <para>
/// <b>Every blocking message carries four things:</b> what is wrong, the two concrete values or
/// paths involved, <i>why the problem is not self-explanatory</i>, and the exact fix. That third
/// part is the MSBL001 lesson: this class of error surfaces far from its cause and reads as a bug
/// in the target, so the message has to carry the explanation rather than assume it.
/// </para>
/// </summary>
public static class EfPreflight
{
    private const string Csproj = "src/FlowLens.Core/FlowLens.Core.csproj";

    /// <summary>
    /// Runs before any expensive work and before any target assembly is loaded: is the target
    /// built, and can FlowLens's EF Core bind against it?
    /// <para>
    /// The ordering is the point. A version skew detected after the model read surfaces as an
    /// exception from inside EF; detected here it is a sentence naming both versions and the edit
    /// that fixes it - and the caller has not yet spent thirty seconds walking the call graph.
    /// </para>
    /// </summary>
    public static EfPreflightReport BeforeRead(Solution solution, string solutionDirectory) =>
        BeforeRead(DbContextDiscovery.FindBuildOutput(solution, solutionDirectory), solution.FilePath);

    public static EfPreflightReport BeforeRead(TargetBuildOutput buildOutput, string? solutionPath) =>
        BeforeRead(buildOutput, solutionPath, EfVersionGate.LoadedVersion);

    /// <param name="flowLensVersionLookup">
    /// Seam for tests. The blocking path is the one that matters most and the hardest to reach by
    /// accident, so it has to be reachable on purpose.
    /// </param>
    public static EfPreflightReport BeforeRead(
        TargetBuildOutput buildOutput,
        string? solutionPath,
        Func<string, Version?> flowLensVersionLookup)
    {
        if (!buildOutput.IsUsable || buildOutput.HostAssemblyPath is not { } hostAssemblyPath)
        {
            return Block(
                "Hedef repo derlenmemis - EF modeli okunamaz, graph yazilmadi.",
                [("durum", buildOutput.Problem ?? "derlenmis cikti bulunamadi")],
                "tablo ve kolon adlari EF Core'un IModel'inden okunuyor, bu da hedefin DERLENMIS " +
                "DbContext'lerini gerektiriyor. Faz 1 ve 2 yalniz kaynak kodu istiyordu; Faz 3'un " +
                "getirdigi yeni on kosul bu.",
                $"dotnet build \"{solutionPath ?? "<hedef>.sln\""}\"");
        }

        var gate = EfVersionGate.Check(hostAssemblyPath, flowLensVersionLookup);
        if (gate.Passed)
        {
            return EfPreflightReport.Clear;
        }

        var problem = gate.Problems.First();
        var package = EfVersionGate.RemediationPackage(problem.PackageId);

        return Block(
            "EF Core surum uyusmazligi - model okunamaz, graph yazilmadi.",
            [
                ("paket", problem.PackageId),
                ("hedef", $"{Describe(problem.TargetVersion),-10} ({EfVersionGate.DepsJsonPath(hostAssemblyPath)})"),
                ("FlowLens", $"{Describe(problem.FlowLensVersion),-10} ({Csproj})"),
                ("fark", problem.Problem ?? "uyusmuyor"),
            ],
            ".NET'in TPA listesi assembly'leri BASIT ISIMLE eslestirir ve surumu umursamaz. " +
            "Uyusmayan surum sessizce baglanir, sonra model kurulurken alakasiz bir noktada " +
            "MissingMethodException veya TypeLoadException olarak patlar - sebebinden cok uzakta.",
            $"{Csproj} icinde surumu yukseltin:" + Environment.NewLine +
            $"    <PackageReference Include=\"{package}\" Version=\"{Describe(problem.TargetVersion)}\" />" +
            Environment.NewLine +
            "  Hedefin EF surumu FlowLens'inkiyle hizalanamiyorsa docs/known-limitations.md L14'e bakin.");
    }

    /// <summary>
    /// Runs after the read: did every declared context actually produce a model?
    /// <para>
    /// The dangerous case is not total failure, it is partial success. Seven contexts out of eight
    /// yields a well-formed graph that is simply missing a module's tables, and nothing about the
    /// output would suggest that anything went wrong.
    /// </para>
    /// </summary>
    public static EfPreflightReport AfterRead(
        EfModelReadResult result,
        IReadOnlyList<DbContextDeclaration> declared)
    {
        foreach (var assembly in result.UnresolvedAssemblies)
        {
            return Block(
                "EF modeli yuklenemedi - assembly cozulemedi, graph yazilmadi.",
                [("assembly", assembly)],
                "EF, ApplyConfigurationsFromAssembly icinde ReflectionTypeLoadException'i YUTAR ve " +
                "yukleyemedigi tipleri sessizce atar. Dusen bir IEntityTypeConfiguration, sessizce " +
                "kaybolan bir tablo demektir - hicbir hata gorunmez.",
                "paylasilan cerceveye ait bir assembly ise " + Csproj + " dosyasina uygun " +
                "<FrameworkReference> ekleyin; degilse hedefi yeniden derleyin.");
        }

        foreach (var typeLoad in result.Warnings)
        {
            return Block(
                "EF modeli eksik yuklendi - bazi tipler yuklenemedi, graph yazilmadi.",
                [("ayrinti", typeLoad)],
                "yuklenemeyen tipler arasinda bir IEntityTypeConfiguration varsa o tablo modelde hic " +
                "olusmaz ve EF bunu hata olarak bildirmez.",
                "eksik bagimliligi " + Csproj + " uzerinden saglayin veya hedefi yeniden derleyin.");
        }

        foreach (var failure in result.Failures)
        {
            return Block(
                $"EF modeli okunamadi: {Short(failure.ContextClrTypeName)} - graph yazilmadi.",
                [("bildirim", failure.Location), ("sebep", failure.Reason)],
                "bu modulun tum tablo ve kolonlari graph'ta eksik olurdu ve eksiklik, " +
                "\"bu akis hicbir tabloya dokunmuyor\" cevabindan ayirt edilemez.",
                "hedefi yeniden derleyin; sorun surerse bu context okuyucunun saglamadigi " +
                "constructor argumanlari istiyor demektir.");
        }

        if (result.Snapshots.Count != declared.Count)
        {
            return Block(
                "EF modeli eksik - graph yazilmadi.",
                [
                    ("Roslyn buldugu", $"{declared.Count} DbContext"),
                    ("okunabilen", $"{result.Snapshots.Count} DbContext"),
                ],
                "Roslyn neyin VAR OLDUGUNU, reflection neyin YUKLENDIGINI bilir. Aradaki fark, bir " +
                "modulun tablolarinin graph'ta hic bulunmamasi demek.",
                "hedefi yeniden derleyin, boylece her modul assembly'si guncel olur.");
        }

        var empty = result.Snapshots.FirstOrDefault(s => s.Entities.All(e => e.QualifiedTableName is null));
        if (empty is not null)
        {
            return Block(
                $"EF modeli bos: {Short(empty.ContextClrTypeName)} hicbir tablo uretmedi - graph yazilmadi.",
                [("context", empty.ContextClrTypeName)],
                "sifir tablo bir sonuc degil, bir arizadir: konfigurasyon siniflari yuklenememis olabilir.",
                "hedefi yeniden derleyin ve bu context'in OnModelCreating'ini kontrol edin.");
        }

        return EfPreflightReport.Clear;
    }

    /// <summary>
    /// Assembles the four-part block. Kept in one place so no failure path can accidentally ship a
    /// bare exception message with no remedy in it.
    /// </summary>
    private static EfPreflightReport Block(
        string headline,
        IReadOnlyList<(string Label, string Value)> facts,
        string why,
        string fix)
    {
        var width = facts.Max(f => f.Label.Length);

        var lines = new List<string> { headline, string.Empty };
        lines.AddRange(facts.Select(f => $"  {f.Label.PadRight(width)} : {f.Value}"));
        lines.Add(string.Empty);
        lines.Add($"  Neden burada duruyoruz: {why}");
        lines.Add(string.Empty);
        lines.Add($"  Cozum: {fix}");

        return new EfPreflightReport(true, string.Join(Environment.NewLine, lines));
    }

    private static string Describe(Version? version) => version?.ToString() ?? "(yok)";

    private static string Short(string clrTypeName) =>
        clrTypeName[(clrTypeName.LastIndexOf('.') + 1)..];
}
