using System.Globalization;
using System.Text;
using FlowLens.Core.Answers;

namespace FlowLens.Core.Triage;

/// <summary>
/// Renders a <see cref="TriageReport"/> as markdown.
/// <para>
/// Rendering is separate from building for the same reason <see cref="TraceAnswer"/> lives in Core:
/// the report is data, and markdown is one of two presentations of it. Nothing here decides
/// anything - if a fact is not in the record, it cannot appear on the page.
/// </para>
/// <para>
/// No timestamp anywhere. Phase 5's rule: an artefact carrying its own generation time can never be
/// byte-identical twice. The git half is pinned by HEAD's sha instead, which says WHICH commit the
/// report describes rather than when it was written.
/// </para>
/// </summary>
public static class TriageMarkdown
{
    public static string Render(TriageReport report)
    {
        var page = new StringBuilder();

        Title(page, report);
        Input(page, report);
        Sources(page, report);
        ErrorPoint(page, report);
        Frames(page, report);
        EntryPoints(page, report);
        Downstream(page, report);
        Limitations(page, report);
        Commits(page, report);

        return page.ToString().ReplaceLineEndings("\n");
    }

    private static void Title(StringBuilder page, TriageReport report)
    {
        page.AppendLine("<!-- ÜRETİLMİŞ RAPOR — `flowlens triage` çıktısı. -->");
        page.AppendLine();
        page.AppendLine("# Incident raporu");
        page.AppendLine();
        page.AppendLine("> Bu bir rapordur, bir düzeltme değil. FlowLens dal açmaz, yama yazmaz ve");
        page.AppendLine("> hiçbir git yazma işlemi yapmaz — gerekçe `docs/design-decisions.md`.");
        page.AppendLine();
    }

    private static void Input(StringBuilder page, TriageReport report)
    {
        page.AppendLine("## Girdi");
        page.AppendLine();
        page.AppendLine($"**`{Escape(report.ExceptionType)}`**");
        page.AppendLine();

        if (report.Message.Length > 0)
        {
            page.AppendLine($"> {Escape(report.Message)}");
            page.AppendLine();
        }

        var counts = report.Counts;

        page.AppendLine("| Yığın izi satırı | adet |");
        page.AppendLine("|---|---:|");
        page.AppendLine($"| Çerçeve | {counts.Frames} |");
        page.AppendLine($"| ...proje dışı | {counts.Foreign} ({counts.DistinctForeign} farklı metot) |");
        page.AppendLine($"| Ayraç | {counts.Separators} |");
        page.AppendLine($"| Metin (başlık, `Exception data`) | {counts.Text} |");
        page.AppendLine($"| **Ayrıştırılamayan** | **{counts.Unparsed}** |");
        page.AppendLine();

        if (report.UnparsedLines.Count > 0)
        {
            page.AppendLine("Ayrıştırılamayan satırlar tam metinleriyle duruyor — sessizce atılmadılar:");
            page.AppendLine();

            foreach (var line in report.UnparsedLines)
            {
                page.AppendLine($"- `{Escape(line)}`");
            }

            page.AppendLine();
        }
    }

    private static void Sources(StringBuilder page, TriageReport report)
    {
        page.AppendLine("## Kaynaklar");
        page.AppendLine();
        page.AppendLine("| | |");
        page.AppendLine("|---|---|");
        page.AppendLine($"| Graph | `{Escape(report.GraphPath)}` — {report.GraphNodes} node, {report.GraphEdges} kenar |");

        var repo = report.Repo;

        var origin = repo.Origin switch
        {
            RepoOrigin.Given => "`--repo` ile verildi",
            RepoOrigin.DerivedFromStackTrace => "yığın izinden türetildi",
            _ => "**bulunamadı**",
        };

        page.AppendLine($"| Repo | {(repo.Found ? $"`{Escape(repo.Root)}`" : "—")} ({origin}) |");

        if (repo.Evidence.Length > 0)
        {
            page.AppendLine($"| Türetme | `{Escape(repo.Evidence)}` |");
        }

        if (report.Commits.Git.Head.Length > 0)
        {
            page.AppendLine($"| HEAD | `{Escape(report.Commits.Git.Head)}` |");
        }

        page.AppendLine();

        if (repo.Error is { Length: > 0 } error)
        {
            page.AppendLine($"⚠ {Escape(error)}");
            page.AppendLine();

            if (repo.Attempts.Count > 0)
            {
                page.AppendLine("Denenen kökler:");
                page.AppendLine();

                foreach (var attempt in repo.Attempts)
                {
                    page.AppendLine($"- `{Escape(attempt)}`");
                }

                page.AppendLine();
            }
        }
    }

    private static void ErrorPoint(StringBuilder page, TriageReport report)
    {
        page.AppendLine("## Hata noktası");
        page.AppendLine();

        if (report.ErrorPoint is not { } node)
        {
            page.AppendLine($"**Yok.** {Escape(report.ErrorPointMissing)}");
            page.AppendLine();
            page.AppendLine("Bu, \"hiçbir şeye dokunmuyor\" demek DEĞİL — graph bu çerçeveyi tanımıyor demek.");
            page.AppendLine();
            return;
        }

        page.AppendLine($"**{Escape(node.DisplayName)}** — {Escape(node.Module)} · `{Escape(node.Location)}`");
        page.AppendLine();
        page.AppendLine($"`{Escape(node.Id)}`");
        page.AppendLine();

        if (report.ErrorPointDiagnostics.Count == 0)
        {
            return;
        }

        var exact = report.ErrorPointDiagnostics.Where(d => d.ExactLine).ToList();

        page.AppendLine(exact.Count > 0
            ? "### ⚠ Hata noktası, graph'ın bakamadığı bir bölgede"
            : "### Hata noktasının dosyasında duran build uyarıları");
        page.AppendLine();

        foreach (var hit in report.ErrorPointDiagnostics)
        {
            var mark = hit.ExactLine ? "**tam bu satırda**" : "aynı dosyada";
            page.AppendLine($"- {mark} — `{Escape(hit.Diagnostic)}`");
        }

        page.AppendLine();

        if (exact.Count > 0)
        {
            page.AppendLine("Yani aşağıdaki tablo listesi eksik olabilir: o erişim graph'a hiç girmedi.");
            page.AppendLine();
        }
    }

    private static void Frames(StringBuilder page, TriageReport report)
    {
        page.AppendLine("## Çerçeveler");
        page.AppendLine();
        page.AppendLine("> Doğrulama, yığın izindeki çağrının **kaynakta yazıldığı yeri** gösterir;");
        page.AppendLine("> hangi dalın koştuğunu söylemez. `graph'ta yok`, çağrının olmadığı anlamına");
        page.AppendLine("> gelmez — FlowLens'in o çerçeveyi görmediği anlamına gelir.");
        page.AppendLine();

        // Keyed by CALLEE: the row shows how the frame ABOVE it (its caller) reaches this frame,
        // which is the question a reader has while looking at this line.
        var links = report.Links.ToDictionary(l => l.CalleeIndex);

        page.AppendLine("| # | Çerçeve | Konum | Hüküm | Çağıranından bağ |");
        page.AppendLine("|---:|---|---|---|---|");

        foreach (var match in report.Frames)
        {
            var frame = match.Frame;

            var verdict = match.Verdict switch
            {
                FrameVerdict.Matched => "eşleşti",
                FrameVerdict.Ambiguous => $"**belirsiz** ({match.Candidates.Count} aday)",
                FrameVerdict.NotInGraph => "**graph'ta yok**",
                _ => "proje dışı",
            };

            var link = links.TryGetValue(frame.Index, out var found) ? Describe(found) : "—";

            page.AppendLine(
                $"| {frame.Index} | `{Escape(frame.Key)}` | {(frame.HasLocation ? $"`{Escape(Short(frame.FilePath))}:{frame.Line}`" : "—")} " +
                $"| {verdict} | {link} |");
        }

        page.AppendLine();

        var ambiguous = report.Frames.Where(f => f.Verdict == FrameVerdict.Ambiguous).ToList();

        if (ambiguous.Count > 0)
        {
            page.AppendLine("### Belirsiz çerçeveler — hiçbiri seçilmedi");
            page.AppendLine();

            foreach (var match in ambiguous)
            {
                page.AppendLine($"`{Escape(match.Frame.Key)}` için {match.Candidates.Count} aday:");
                page.AppendLine();

                foreach (var candidate in match.Candidates)
                {
                    page.AppendLine($"- `{Escape(candidate.Id)}`");
                }

                page.AppendLine();
            }
        }
    }

    private static string Describe(FrameLink link) => link.Verdict switch
    {
        LinkVerdict.Verified when link.Through.Length > 0 => "doğrulandı (arayüz köprüsü)",
        LinkVerdict.Verified => "doğrulandı",
        LinkVerdict.LineMismatch =>
            $"satır eşleşmedi (graph: {string.Join(", ", link.KnownLines)})",
        LinkVerdict.SkippedFrames =>
            $"graph'ta yok — {link.Path.Count} hop'luk yol var, atlanmış çerçeve olabilir",
        LinkVerdict.MissingEdge => "graph'ta yok",
        LinkVerdict.SameMethod => "aynı metot (kenar beklenmiyor)",
        _ => "—",
    };

    private static void EntryPoints(StringBuilder page, TriageReport report)
    {
        page.AppendLine("## Giriş noktaları");
        page.AppendLine();

        if (report.EntryPoints is not { } answer)
        {
            page.AppendLine("Hata noktası bulunamadığı için sorulamadı.");
            page.AppendLine();
            return;
        }

        if (answer.Total == 0)
        {
            page.AppendLine("**Hiçbir endpoint, consumer veya background job buraya ulaşmıyor.**");
            page.AppendLine();
            return;
        }

        var summary = string.Join(
            " + ",
            answer.Groups.Select(g => $"{g.Count} {Turkish(g.RootKind)}"));

        page.AppendLine($"**{answer.Total}** — {summary}");
        page.AppendLine();

        foreach (var group in answer.Groups)
        {
            page.AppendLine($"**{Turkish(group.RootKind)}** ({group.Count})");
            page.AppendLine();

            foreach (var root in group.Nodes)
            {
                page.AppendLine($"- `{Escape(root.DisplayName)}` — {Escape(root.Module)} · `{Escape(root.Location)}`");
            }

            page.AppendLine();
        }
    }

    private static void Downstream(StringBuilder page, TriageReport report)
    {
        page.AppendLine("## Bu noktadan sonra dokunulan tablolar");
        page.AppendLine();

        if (report.Downstream is not { } data)
        {
            page.AppendLine("Hata noktası bulunamadığı için sorulamadı.");
            page.AppendLine();
            return;
        }

        if (data.Tables.Count == 0)
        {
            // One line on purpose: a "**...**" continuation line after a non-blank one is markdown
            // lazy continuation, which parses fine and renders wrong - the Phase 5 defect class.
            page.AppendLine(
                "**Hiçbiri.** Bunun \"veriye dokunmuyor\" mu yoksa \"bakamadım\" mı olduğunu "
                + "**Bilinen sınırlar** bölümü söyler.");
            page.AppendLine();
            return;
        }

        page.AppendLine("| Tablo | Erişim | Kolon | Tanım |");
        page.AppendLine("|---|---|---:|---|");

        foreach (var table in data.Tables)
        {
            page.AppendLine(
                $"| `{Escape(table.Table)}` | {Escape(table.Access)} | {table.Columns.Count} | `{Escape(table.Location)}` |");
        }

        page.AppendLine();
    }

    private static void Limitations(StringBuilder page, TriageReport report)
    {
        if (report.Limitations.Count == 0)
        {
            return;
        }

        page.AppendLine("## Bilinen sınırlar");
        page.AppendLine();

        foreach (var limitation in report.Limitations)
        {
            page.Append($"- **{Escape(limitation.Code)}** — {Escape(limitation.Message)}");

            if (limitation.Locations.Count > 0)
            {
                page.Append("<br>");
                page.Append(string.Join(", ", limitation.Locations.Select(l => $"`{Escape(l)}`")));
            }

            page.AppendLine();
        }

        page.AppendLine();
    }

    private static void Commits(StringBuilder page, TriageReport report)
    {
        var section = report.Commits;

        page.AppendLine("## Son değişiklikler");
        page.AppendLine();

        if (!section.Git.Available)
        {
            page.AppendLine($"⚠ **git okunamadı** — {Escape(section.Git.Error ?? "sebep bildirilmedi")}");
            page.AppendLine();

            // The exit code is stated only when git is the ONLY thing missing. An unresolved report
            // exits 4, and announcing 3 here would be a claim the run then contradicts - the report
            // must not describe a run that did not happen.
            page.AppendLine(report.Unresolved
                ? "Hata noktası da bulunamadığı için bu koşu zaten eksik; çıkış kodu **4**."
                : "Raporun geri kalanı geçerli: kökler, tablolar ve sınırlar yalnız `graph.json`'dan "
                  + "gelir. Eksik olan yalnız commit geçmişi ve HEAD. Çıkış kodu **3**.");

            page.AppendLine();
            return;
        }

        page.AppendLine($"`{section.FileCount}` dosya, `{section.CommitLines}` commit satırı — `git log --oneline -5`");
        page.AppendLine();

        foreach (var history in section.Git.Files)
        {
            page.AppendLine($"**`{Escape(history.FilePath)}`**");
            page.AppendLine();

            if (history.Error is { Length: > 0 } error)
            {
                page.AppendLine($"- ⚠ {Escape(error)}");
                page.AppendLine();
                continue;
            }

            if (history.Commits.Count == 0)
            {
                page.AppendLine("- (bu dosya için commit yok)");
                page.AppendLine();
                continue;
            }

            foreach (var commit in history.Commits)
            {
                page.AppendLine($"- `{Escape(commit.Sha)}` {Escape(commit.Subject)}");
            }

            page.AppendLine();
        }
    }

    private static string Turkish(RootKind kind) => kind switch
    {
        RootKind.Endpoint => "endpoint",
        RootKind.Consumer => "consumer",
        RootKind.BackgroundService => "background job",
        _ => "kök",
    };

    /// <summary>Last two path segments - enough to recognise a file, short enough for a table cell.</summary>
    private static string Short(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');

        return parts.Length <= 2
            ? path
            : string.Join('/', parts[^2..]);
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);
}
