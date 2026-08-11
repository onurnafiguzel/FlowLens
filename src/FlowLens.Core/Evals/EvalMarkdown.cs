using System.Globalization;
using System.Text;

namespace FlowLens.Core.Evals;

/// <summary>
/// Renders the scorecard as markdown.
/// <para>
/// Deterministic by construction: no timestamp, no elapsed time, every list ordered ordinally. Phase
/// 5's rule - an artefact that records when it was generated can never be byte-identical twice, and a
/// report that cannot be diffed cannot show a recall drop.
/// </para>
/// </summary>
public static class EvalMarkdown
{
    public static string Render(EvalScorecard card)
    {
        var text = new StringBuilder();

        Header(text, card);
        Metrics(text, card);
        Evidence(text, card);
        Categories(text, card);
        Boxes(text, card);
        Questions(text, card);
        MetaTable(text, card);
        Unmeasurable(text, card);
        Oracle(text, card);

        return text.ToString();
    }

    // ---------------------------------------------------------------- 1

    private static void Header(StringBuilder text, EvalScorecard card)
    {
        var run = card.Run;

        text.Append("# FlowLens — Faz 7 eval raporu\n\n");
        text.Append(
            "Bu rapor testlerin cevabını vermez. Testler kodun **çalıştığını** doğrular; buradaki "
            + "sayılar cevabın **doğru ve tam olduğunu** ölçer. Beklenen değerler ModularCommerce "
            + "kaynağından elle çıkarıldı ve `evals/questions.json` runner yazılmadan ÖNCE "
            + "commit'lendi.\n\n");

        text.Append("## 1. Kaynak\n\n");
        text.Append("| | |\n|---|---|\n");
        text.Append($"| Graph | `{run.GraphPath}` |\n");
        text.Append($"| Düğüm / kenar | {run.GraphNodes} / {run.GraphEdges} |\n");
        text.Append($"| Soru | {run.Results.Count} |\n");
        text.Append($"| Çözülemeyen selector | {card.Unresolved.Count} |\n");
        text.Append($"| EF dışı tablo | {run.EfOutsideTables.Count} — {Join(run.EfOutsideTables)} |\n\n");

        text.Append("> **EF içi / EF dışı nasıl ayrıldı:** bir tabloya EF'in kendisinin SQL ürettiği "
            + "bir mekanizmayla (`DbSetProperty`, `SetOfT`, `FluentChainHead`, "
            + "`ExecuteUpdateSetProperty`, `SaveChangesInterceptor`, `OwnedCollectionAdd`, "
            + "`RowInsert`) ulaşılıyorsa **EF içi**. Yalnız inşadan ya da change-tracker "
            + "çıkarımından ulaşılıyorsa **EF dışı**. Elle liste değil, graph'tan türetiliyor.\n\n");

        text.Append($"> **Oracle'ın kolon kuralı:** {card.Run.Set.ColumnRule}\n\n");

        if (card.Unresolved.Count > 0)
        {
            text.Append("**Çözülemeyen selector'lar** — bunlar kaçırma değil, BOZUK SORU:\n\n");

            foreach (var line in card.Unresolved)
            {
                text.Append($"- {line}\n");
            }

            text.Append('\n');
        }
    }

    // ---------------------------------------------------------------- 2

    private static void Metrics(StringBuilder text, EvalScorecard card)
    {
        text.Append("## 2. Metrikler\n\n");
        text.Append("Recall önceliklidir: eksik bir kolon, fazladan bir kolondan tehlikelidir.\n\n");
        text.Append("| Seviye | Kapsam | Beklenen | Bulunan | Recall | Dönen | Fazladan | Precision |\n");
        text.Append("|---|---|---:|---:|---:|---:|---:|---:|\n");

        foreach (var row in card.Metrics)
        {
            text.Append(
                $"| {row.Level} | {Scope(row.Scope)} | {row.Expected} | {row.Found} | "
                + $"{Percent(row.Recall)} | {row.Actual} | {row.FalsePositive} | {Percent(row.Precision)} |\n");
        }

        text.Append('\n');
        text.Append("> **`kolon-yazma` ve `kolon-okuma` toplanmaz.** `AnswerBuilder.ColumnsByTable` "
            + "yalnız `Writes` kenarlarına bakıyor, dolayısıyla okunan bir kolonun recall'ı YAPISAL "
            + "olarak 0. İkisini tek sayıya indirmek, yazma recall'ını ilgisiz bir sebeple aşağı "
            + "çeker ve F9'un gerçek boyutunu gizlerdi.\n\n");
        text.Append("> `sınır kodu` satırı bir **varlık** iddiasıdır, küme eşitliği değil: sorular "
            + "bulunması ZORUNLU kodları sayar, cevabın taşıyabileceği kodların tamamını değil. "
            + "Bu yüzden precision hesaplanmaz.\n\n");
        text.Append("> `tablo (erisim R/W)` satırının popülasyonu **bulunan tablolardır**, "
            + "uyuşmazlıklar değil. `Bulunan` sütunu erişimi doğru raporlanan tablo sayısıdır; "
            + "aradaki fark uyuşmazlık sayısıdır ve her biri §6'da adıyla yazılıdır. Bir tablo "
            + "bulunamadıysa erişimi hiç kontrol edilmez — aynı kayıp iki kez sayılmaz.\n\n");
    }

    // ---------------------------------------------------------------- 3

    private static void Evidence(StringBuilder text, EvalScorecard card)
    {
        var tally = card.Evidence;

        text.Append("## 3. Kanıt skoru — üç sonuç\n\n");
        text.Append("Doğru cevap ile doğru sebep aynı şey değil. İkiye indirgenirse F7 sınıfı ya "
            + "kayıp görünür (yanlış) ya kaybolur (yanlış).\n\n");
        text.Append("| Sonuç | Anlamı | Adet | Pay |\n|---|---|---:|---:|\n");
        text.Append($"| `beklenen-mekanizmayla` | doğru cevap, doğru kanıt (`Direct` / `RowLevel`) | "
            + $"{tally.ExpectedMechanism} | {Share(tally.ExpectedMechanism, tally.Total)} |\n");
        text.Append($"| `farklı-ama-geçerli` | doğru cevap, ikinci sınıf kanıt (`Inferred` / "
            + $"`SecondClass`) | {tally.DifferentButValid} | {Share(tally.DifferentButValid, tally.Total)} |\n");
        text.Append($"| `bulunamadı` | recall kaybı | {tally.NotFound} | {Share(tally.NotFound, tally.Total)} |\n\n");
        text.Append("> Yalnız **yazma** kolonları üzerinden hesaplanır. Okunan kolon hiçbir mekanizma "
            + "taşımaz, dolayısıyla hepsi `bulunamadı` olur ve recall satırını tekrarlamaktan başka "
            + "bir şey söylemezdi.\n\n");
    }

    // ---------------------------------------------------------------- 4

    private static void Categories(StringBuilder text, EvalScorecard card)
    {
        text.Append("## 4. Kategori kırılımı — popülasyonla birlikte\n\n");
        text.Append("| Sınıf | Soru | Popülasyon | Temsilci mi | Beklenen | Bulunan | Kaçırılan |\n");
        text.Append("|---|---:|---:|---|---:|---:|---:|\n");

        foreach (var row in card.Categories)
        {
            var representative = row.Representative
                ? "evet"
                : $"**HAYIR** — tek örnek, kategori değil o örnek ölçüldü";

            text.Append(
                $"| {row.PopulationClass} | {row.Questions} | {row.PopulationCount} | {representative} | "
                + $"{row.Expected} | {row.Found} | {row.Missed} |\n");
        }

        text.Append('\n');
    }

    // ---------------------------------------------------------------- 5

    private static void Boxes(StringBuilder text, EvalScorecard card)
    {
        text.Append("## 5. Öngörü kutuları — 3×2\n\n");
        text.Append("Birim **soru**, öngörü değil: bir öngörüyü belirli bir kaçırmaya bağlamak "
            + "graph'ın taşımadığı bir eşleme gerektirirdi ve yedi öngörüye tek kayıp için kredi "
            + "vermek olurdu. Tek tek atıf §6'daki tablodan elle yapılabilir.\n\n");
        text.Append("| | gerçekleşti | gerçekleşmedi |\n|---|---|---|\n");

        foreach (var row in (string[])[EvalScore.PredictedOpen, EvalScore.PredictedClosed, EvalScore.NotPredicted])
        {
            var realized = card.Boxes.First(b => b.Row == row && b.Realized);
            var not = card.Boxes.First(b => b.Row == row && !b.Realized);

            text.Append($"| **{row}** | {Cell(realized)} | {Cell(not)} |\n");
        }

        text.Append('\n');
        text.Append("> Sol-alt kutu (öngörülmedi + gerçekleşti) doluysa eval işini yapmıştır: "
            + "çıktıyı kopyalayarak bir kaçırma öngörüsü üretilemez.\n\n");

        static string Cell(BoxCell cell) =>
            cell.Questions.Count == 0
                ? $"— ({cell.Meaning})"
                : $"**{cell.Questions.Count}** · {string.Join(", ", cell.Questions)}<br>{cell.Meaning}";
    }

    // ---------------------------------------------------------------- 6

    private static void Questions(StringBuilder text, EvalScorecard card)
    {
        text.Append("## 6. Soru soru\n\n");

        foreach (var result in card.Run.Results)
        {
            var question = result.Question;

            text.Append($"### {question.Id} — {question.Question}\n\n");
            text.Append($"`{question.Selector.Node}` · {question.Selector.Direction} · "
                + $"kategori `{question.Category}` · popülasyon `{question.Population.Class}` = "
                + $"{question.Population.Count}"
                + (question.Population.Representative ? string.Empty : " (**temsilci değil**)")
                + "\n\n");

            if (!result.Resolved)
            {
                text.Append($"**BOZUK SORU** — {result.ResolutionError}\n\n");
                continue;
            }

            if (result.Comparisons.Count > 0)
            {
                text.Append("| Eksen | Kapsam | Beklenen | Bulunan | Fazladan |\n|---|---|---:|---:|---:|\n");

                foreach (var comparison in result.Comparisons)
                {
                    text.Append(
                        $"| {EvalRunner.Label(comparison.Kind)} — {comparison.Subject} | "
                        + $"{Scope(comparison.Scope)} | {comparison.ExpectedCount} | "
                        + $"{comparison.FoundCount} | "
                        + $"{(comparison.PresenceOnly ? "—" : comparison.Unexpected.Count.ToString(CultureInfo.InvariantCulture))} |\n");
                }

                text.Append('\n');
            }

            if (question.ExpectedToFail.Count > 0)
            {
                text.Append("**Öngörülen kaçırmalar:** "
                    + string.Join(" · ", question.ExpectedToFail
                        .OrderBy(p => p.Id, StringComparer.Ordinal)
                        .Select(p => p.ClosedIn is { Length: > 0 } closed
                            ? $"`{p.Id}` (kapanmıştı: {closed})"
                            : $"`{p.Id}`"))
                    + "\n\n");
            }

            if (!result.MissRealized)
            {
                text.Append("Kaçırma **yok**.\n\n");
                continue;
            }

            text.Append($"**Oracle:** `{result.OracleVerdict}`"
                + (result.OracleEvidence.Length > 0 ? $" — kanıt: {result.OracleEvidence}" : string.Empty)
                + "\n\n");

            if (result.Misses.Count > 0)
            {
                text.Append("Gerçekleşen kaçırmalar:\n\n");

                foreach (var miss in result.Misses)
                {
                    text.Append($"- {miss}\n");
                }

                text.Append('\n');
            }

            if (result.FalsePositives.Count > 0)
            {
                text.Append("Fazladan gelenler (precision kaybı):\n\n");

                foreach (var extra in result.FalsePositives)
                {
                    text.Append($"- {extra}\n");
                }

                text.Append('\n');
            }
        }
    }

    // ---------------------------------------------------------------- 7

    private static void MetaTable(StringBuilder text, EvalScorecard card)
    {
        text.Append("## 7. Meta-test — F1..F10 ve L1..L22\n\n");
        text.Append("Boş satır, gerekçesi yazılmadıkça eval set'in eksik olduğu anlamına gelir.\n\n");
        text.Append("| Sınıf | Görünür kılan soru | Gerekçeli boşluk |\n|---|---|---|\n");

        foreach (var row in card.Meta)
        {
            text.Append(
                $"| {row.Id} | {(row.Questions.Count == 0 ? "—" : string.Join(", ", row.Questions))} | "
                + $"{(row.Reason.Length == 0 ? "—" : row.Reason)} |\n");
        }

        text.Append('\n');
    }

    // ---------------------------------------------------------------- 8

    private static void Unmeasurable(StringBuilder text, EvalScorecard card)
    {
        text.Append("## 8. Ölçülemeyen sınıflar\n\n");
        text.Append("Popülasyonu 0 olan ya da statik bir eval'in yapısal olarak göremeyeceği "
            + "sınıflar. Sessizce atlanmaz — atlanan bir satır \"kapsandı\" diye okunur.\n\n");
        text.Append("| Sınıf | Ad | Neden ölçülemedi |\n|---|---|---|\n");

        foreach (var row in card.Unmeasurable)
        {
            text.Append($"| {row.Id} | {row.Name} | {row.Reason} |\n");
        }

        text.Append('\n');
    }

    // ---------------------------------------------------------------- 9

    private static void Oracle(StringBuilder text, EvalScorecard card)
    {
        var oracle = card.Oracle;

        text.Append("## 9. Oracle çapraz kontrolü\n\n");
        text.Append("Eval \"kaçırma\" dediğinde iki hipotez var: tool kaçırdı, ya da elle çıkarılan "
            + "beklenen değer yanlıştı. İkincisi eval set'in KENDİ kusurudur ve ayrı sayılır.\n\n");
        text.Append("| Sonuç | Adet |\n|---|---:|\n");
        text.Append($"| `{EvalOracle.Confirmed}` — beklenen değer kaynakta var, kaçırma tool'a ait | {oracle.Confirmed} |\n");
        text.Append($"| `{EvalOracle.Corrected}` — beklenen değer yanlıştı, **bulgu** | {oracle.Corrected} |\n");
        text.Append($"| `{EvalOracle.Pending}` — çapraz kontrol henüz yapılmadı | {oracle.Pending} |\n\n");
        text.Append("> Bir düzeltme `evals/oracle-verdicts.json`'a yazılır ve ModularCommerce "
            + "`file:line` kanıtı taşımak ZORUNDADIR — çıktı bir gerekçe değildir. Düzeltme "
            + "`questions.json`'a AYRI bir commit'te girer, runner commit'ine karışmaz; böylece "
            + "\"beklenen değer çıktıya uydurulmuş mu?\" sorusu tek bir `git log` ile cevaplanır.\n");
    }

    // ---------------------------------------------------------------- helpers

    private static string Scope(EfScope scope) => scope switch
    {
        EfScope.Inside => "EF içi",
        EfScope.Outside => "EF dışı",
        _ => "—",
    };

    private static string Percent(double? value) =>
        value is null ? "—" : "%" + (value.Value * 100).ToString("F1", CultureInfo.InvariantCulture);

    private static string Share(int part, int total) =>
        total == 0 ? "—" : "%" + (100.0 * part / total).ToString("F1", CultureInfo.InvariantCulture);

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "—" : string.Join(", ", values.Select(v => $"`{v}`"));
}
