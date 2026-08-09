using System.Globalization;
using System.Text;

namespace FlowLens.Core.Docs;

/// <param name="Endpoint">Generate only this flow. Null means all of them.</param>
/// <param name="Module">Generate only this module. Null means all of them.</param>
public sealed record DocsRequest(string OutputDirectory, string? Endpoint = null, string? Module = null)
{
    public bool IsFiltered => Endpoint is not null || Module is not null;
}

public sealed record DocsResult(IReadOnlyList<string> Files, int Flows, int Modules, bool IndexWritten);

/// <summary>
/// Writes the documentation site.
/// <para>
/// Every file is a pure function of graph.json. <b>No generation timestamp anywhere</b> - an
/// artifact that records when it was made can never be byte-identical across two runs, which is
/// the same reason elapsedMs was taken out of graph.json in Phase 4. Freshness comes from the
/// commit, not from a line in the file.
/// </para>
/// </summary>
public static class DocsSite
{
    private const string GeneratedBanner =
        "<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->";

    public static DocsResult Write(CodeGraph graph, IReadOnlyList<string> diagnostics, DocsRequest request)
    {
        var written = new List<string>();

        Directory.CreateDirectory(Path.Combine(request.OutputDirectory, "flows"));
        Directory.CreateDirectory(Path.Combine(request.OutputDirectory, "modules"));

        var moduleGraph = ModuleGraphBuilder.Build(graph);
        var moduleDocs = ModuleDocBuilder.Build(graph, moduleGraph, diagnostics);

        var endpoints = graph.Nodes
            .Where(n => n.Kind == NodeKind.Endpoint)
            .Where(n => request.Endpoint is null || Matches(n, request.Endpoint))
            .Where(n => request.Module is null || string.Equals(n.Module, request.Module, StringComparison.Ordinal))
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .ToList();

        var flows = new List<(Node Endpoint, FlowDiagram Diagram, string Path)>();

        foreach (var endpoint in endpoints)
        {
            var diagram = FlowDiagramBuilder.Build(graph, endpoint.Id, diagnostics);
            var relative = Path.Combine("flows", FileNameFor(endpoint.DisplayName) + ".md");

            Save(request.OutputDirectory, relative, FlowPage(endpoint, diagram), written);
            flows.Add((endpoint, diagram, relative));
        }

        var selectedModules = moduleDocs
            .Where(m => request.Module is null || string.Equals(m.Module, request.Module, StringComparison.Ordinal))
            .Where(m => request.Endpoint is null)
            .ToList();

        foreach (var doc in selectedModules)
        {
            Save(request.OutputDirectory, Path.Combine("modules", doc.Module + ".md"), ModulePage(doc), written);
        }

        if (!request.IsFiltered)
        {
            Save(request.OutputDirectory, Path.Combine("modules", "dependencies.md"),
                DependencyPage(moduleGraph), written);

            Save(request.OutputDirectory, "README.md", IndexPage(flows, selectedModules, moduleGraph), written);
        }

        return new DocsResult(written, flows.Count, selectedModules.Count, !request.IsFiltered);
    }

    // ------------------------------------------------------------------ pages

    private static string FlowPage(Node endpoint, FlowDiagram diagram)
    {
        var page = new StringBuilder();

        page.AppendLine(GeneratedBanner);
        page.AppendLine();
        page.AppendLine(CultureInfo.InvariantCulture, $"# {endpoint.DisplayName}");
        page.AppendLine();
        page.AppendLine(CultureInfo.InvariantCulture, $"**Modül:** {endpoint.Module} · **Tanım:** `{endpoint.Location}`");
        page.AppendLine();

        if (diagram.Edges.Count == 0)
        {
            page.AppendLine("> **Bu endpoint hiçbir veriye dokunmuyor.** Çağrı yok, tablo yok, event yok —");
            page.AppendLine(CultureInfo.InvariantCulture,
                $"> gövdesi yalnız statik bir yanıt döndürüyor (`{endpoint.Location}`).");
            page.AppendLine("> Boş küme burada doğru cevap ve kaynakta doğrulandı.");
            page.AppendLine();
        }

        page.AppendLine(MermaidWriter.Flow(diagram));
        page.AppendLine();

        if (diagram.DataLayer.Tables.Count > 0)
        {
            page.AppendLine("## Veri katmanı");
            page.AppendLine();
            page.AppendLine("| Tablo | Erişim | Kolonlar | Tanım |");
            page.AppendLine("|---|---|---|---|");

            foreach (var table in diagram.DataLayer.Tables)
            {
                var columns = table.Columns.Count == 0
                    ? "—"
                    : string.Join(", ", table.Columns.Select(c => $"`{c.Name}`"));

                page.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{table.Table}` | {(table.Access.Length == 0 ? "—" : table.Access)} | {columns} | `{table.Location}` |");
            }

            page.AppendLine();
        }

        page.AppendLine("## Diyagram neyi göstermiyor");
        page.AppendLine();
        page.AppendLine(CultureInfo.InvariantCulture,
            $"Gösterilen **{diagram.Nodes.Count}** node; ham yürüyüş **{diagram.RawNodeCount}** node'a ulaşıyor. " +
            $"Gizlenen: {diagram.Hidden.Intermediate} ara çağrı, {diagram.Hidden.Utility} utility, " +
            $"{diagram.Hidden.Interfaces} arayüz bildirimi, {diagram.Hidden.Dataless} veriye ulaşmayan dal.");
        page.AppendLine();
        page.AppendLine(CultureInfo.InvariantCulture,
            $"Tam liste: `flowlens trace \"{endpoint.DisplayName}\"`");
        page.AppendLine();

        AppendLimitations(page, [.. diagram.Limitations.Select(l => $"**{l.Code}** — {l.Message}" +
            (l.Locations.Count == 0 ? string.Empty : $"<br>`{string.Join("`, `", l.Locations)}`"))]);

        // Unconditional and claim-free. Six of the 25 diagrams are wider than GitHub's content
        // column, but the generator has no renderer and cannot know which - deciding per page would
        // mean guessing from a proxy, and a guess dressed as a measurement is the one thing this
        // project does not ship. The sentence is true on the pages that fit and on the pages that
        // do not, so it needs no condition and cannot go stale (docs/phase-5-notes.md §9.6).
        page.AppendLine();
        page.AppendLine("> Diyagram dar görünüyorsa tıklayarak büyütebilir veya");
        page.AppendLine(CultureInfo.InvariantCulture,
            $"> [mermaid.live'da açabilirsiniz]({MermaidLive.UrlFor(MermaidWriter.FlowBody(diagram))}).");

        return page.ToString();
    }

    private static string ModulePage(ModuleDoc doc)
    {
        var page = new StringBuilder();

        page.AppendLine(GeneratedBanner);
        page.AppendLine();
        page.AppendLine(CultureInfo.InvariantCulture, $"# {doc.Module}");
        page.AppendLine();

        page.AppendLine("## Endpoint'ler");
        page.AppendLine();

        if (doc.Endpoints.Count == 0)
        {
            page.AppendLine("Bu modülün HTTP endpoint'i yok.");
        }
        else
        {
            page.AppendLine("| Endpoint | Tablo | Tanım | Akış |");
            page.AppendLine("|---|---:|---|---|");

            foreach (var endpoint in doc.Endpoints)
            {
                page.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{endpoint.DisplayName}` | {endpoint.TableCount} | `{endpoint.Location}` | " +
                    $"[diyagram](../flows/{FileNameFor(endpoint.DisplayName)}.md) |");
            }
        }

        page.AppendLine();
        page.AppendLine("## Tablolar");
        page.AppendLine();

        if (doc.Tables.Count == 0)
        {
            page.AppendLine("Bu modülün EF Core modelinde tablosu yok.");
        }
        else
        {
            page.AppendLine("| Tablo | Erişim | Kolonlar | Tanım |");
            page.AppendLine("|---|---|---|---|");

            foreach (var table in doc.Tables)
            {
                var columns = table.Columns.Count == 0
                    ? "—"
                    : string.Join(", ", table.Columns.Select(c => $"`{c}`"));

                page.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{table.Table}` | {(table.Access.Length == 0 ? "—" : table.Access)} | {columns} | `{table.Location}` |");
            }

            page.AppendLine();
            page.AppendLine("`W` = yazılıyor · `R` = okunuyor · kolonlar yalnız bir yazma onları adlandırdığında listelenir.");
        }

        page.AppendLine();
        page.AppendLine("## Event'ler");
        page.AppendLine();

        if (doc.Events.Count == 0)
        {
            page.AppendLine("Bu modül integration event tanımlamıyor.");
        }
        else
        {
            page.AppendLine("| Event | Yayınlanıyor | Tüketiciler | Tanım |");
            page.AppendLine("|---|---|---|---|");

            foreach (var evt in doc.Events)
            {
                var consumers = evt.Consumers.Count == 0
                    ? "**yok**"
                    : string.Join(", ", evt.Consumers.Select(c => $"`{c}`"));

                page.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{evt.DisplayName}` | {(evt.Published ? "evet" : "hayır")} | {consumers} | `{evt.Location}` |");
            }
        }

        page.AppendLine();
        page.AppendLine("## Bağımlılıklar");
        page.AppendLine();
        AppendEdges(page, "Bu modülün dokunduğu modüller", doc.DependsOn, e => e.To);
        AppendEdges(page, "Bu modüle dokunanlar", doc.DependedOnBy, e => e.From);

        AppendLimitations(page, [.. doc.Limitations.Select(l => $"`{l}`")]);

        return page.ToString();
    }

    private static string DependencyPage(ModuleGraph graph)
    {
        var page = new StringBuilder();

        page.AppendLine(GeneratedBanner);
        page.AppendLine();
        page.AppendLine("# Modül bağımlılık grafiği");
        page.AppendLine();
        page.AppendLine("Bir modülün diğerine bağımlı sayılma kuralı, dokunduğu **katmana** dayanır —");
        page.AppendLine("modül adına değil. Katman, node id'sindeki `ModularCommerce.<Modül>.<Katman>`");
        page.AppendLine("segmentinden okunur.");
        page.AppendLine();
        page.AppendLine("| Kategori | Kural | Ok |");
        page.AppendLine("|---|---|---|");
        page.AppendLine("| **Sözleşme çağrısı** — meşru | hedef katman `Contracts`, kenar `CALLS` | düz `-->` |");
        page.AppendLine("| **Event** — meşru, en gevşek bağ | `PUBLISHES` / `CONSUMES` | kesikli `-.->` |");
        page.AppendLine("| **Doğrudan referans** — ⚠ ihlal adayı | hedef katman `Application` / `Infrastructure` / `Domain` | kalın `==>` |");
        page.AppendLine();
        page.AppendLine(MermaidWriter.ModuleGraph(graph));
        page.AppendLine();

        var direct = graph.Edges.Where(e => e.Kind == ModuleEdgeKind.Direct).ToList();

        page.AppendLine("## İhlal adayları");
        page.AppendLine();

        if (direct.Count == 0)
        {
            page.AppendLine("Yok. Modüller arası tüm senkron çağrılar `Contracts` üzerinden gidiyor.");
            page.AppendLine();
        }
        else
        {
            page.AppendLine("`Contracts` dışından doğrudan referanslar. **Bu bir hüküm değil, bir işaret** —");
            page.AppendLine("kasıtlı bir tercih olabilir; kararı okuyan verir.");
            page.AppendLine();

            foreach (var edge in direct)
            {
                page.AppendLine(CultureInfo.InvariantCulture, $"### `{edge.From}` → `{edge.To}`");
                page.AppendLine();

                foreach (var evidence in edge.Evidence)
                {
                    page.AppendLine(CultureInfo.InvariantCulture, $"- `{evidence}`");
                }

                page.AppendLine();
            }
        }

        page.AppendLine("## Shared");
        page.AppendLine();
        page.AppendLine(
            $"`Shared`'a giden **{graph.SharedEdgeCount}** kenar diyagrama çizilmedi: " +
            $"{graph.SharedDependents.Count} modülün tamamı `Shared.Kernel`'e bağlı ve bu tasarım gereği. " +
            "Hepsini çizmek `Shared`'ı her şeye bağlı bir merkez yapar ve hiçbir şey söylemez.");
        page.AppendLine();
        page.AppendLine("**Tersi çizilir:** `Shared` bir modülün içine çağrı yapıyorsa bağımlılık ters yöndedir");
        page.AppendLine("ve ihlal adayı olarak işaretlenir.");

        return page.ToString();
    }

    private static string IndexPage(
        IReadOnlyList<(Node Endpoint, FlowDiagram Diagram, string Path)> flows,
        IReadOnlyList<ModuleDoc> modules,
        ModuleGraph moduleGraph)
    {
        var page = new StringBuilder();

        page.AppendLine(GeneratedBanner);
        page.AppendLine();
        page.AppendLine("# ModularCommerce — akış haritası");
        page.AppendLine();
        page.AppendLine("`flowlens docs` ile `graph.json`'dan üretildi. Elle düzenlenmez; her üretim");
        page.AppendLine("aynı girdiden aynı baytları verir.");
        page.AppendLine();
        page.AppendLine("> **Kapsam uyarısı.** FlowLens'in gördüğü, EF Core'un gördüğüdür. Ham SQL ile");
        page.AppendLine("> erişilen tablolar ve ilişkisel olmayan depolar burada **yok** — ama nerede");
        page.AppendLine("> bakılamadığı ilgili sayfada `file:line` ile yazılı.");
        page.AppendLine();

        page.AppendLine("## Modüller");
        page.AppendLine();
        page.AppendLine("| Modül | Endpoint | Tablo | Event |");
        page.AppendLine("|---|---:|---:|---:|");

        foreach (var module in modules)
        {
            page.AppendLine(CultureInfo.InvariantCulture,
                $"| [{module.Module}](modules/{module.Module}.md) | {module.Endpoints.Count} | " +
                $"{module.Tables.Count} | {module.Events.Count} |");
        }

        page.AppendLine();
        page.AppendLine(CultureInfo.InvariantCulture,
            $"[Modül bağımlılık grafiği](modules/dependencies.md) — {moduleGraph.Edges.Count} kenar, " +
            $"{moduleGraph.Edges.Count(e => e.Kind == ModuleEdgeKind.Direct)} ihlal adayı.");
        page.AppendLine();

        var touching = flows.Where(f => f.Diagram.DataLayer.Tables.Count > 0).ToList();
        var untouched = flows.Where(f => f.Diagram.DataLayer.Tables.Count == 0).ToList();

        page.AppendLine("## Akışlar");
        page.AppendLine();
        page.AppendLine("| Endpoint | Modül | Tablo | Node |");
        page.AppendLine("|---|---|---:|---:|");

        foreach (var (endpoint, diagram, path) in touching)
        {
            page.AppendLine(CultureInfo.InvariantCulture,
                $"| [`{endpoint.DisplayName}`]({path.Replace('\\', '/')}) | {endpoint.Module} | " +
                $"{diagram.DataLayer.Tables.Count} | {diagram.Nodes.Count} |");
        }

        if (untouched.Count > 0)
        {
            page.AppendLine();
            page.AppendLine(CultureInfo.InvariantCulture,
                $"### Veri katmanına dokunmayan ({untouched.Count})");
            page.AppendLine();
            page.AppendLine("Bunlar eksik değil — ölçüldü ve hiçbir tabloya ulaşmıyorlar.");
            page.AppendLine();

            foreach (var (endpoint, _, path) in untouched)
            {
                page.AppendLine(CultureInfo.InvariantCulture,
                    $"- [`{endpoint.DisplayName}`]({path.Replace('\\', '/')}) — `{endpoint.Location}`");
            }
        }

        return page.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private static void AppendEdges(
        StringBuilder page,
        string title,
        IReadOnlyList<ModuleEdge> edges,
        Func<ModuleEdge, string> other)
    {
        page.AppendLine(CultureInfo.InvariantCulture, $"**{title}:**");
        page.AppendLine();

        if (edges.Count == 0)
        {
            page.AppendLine("yok.");
        }
        else
        {
            foreach (var edge in edges)
            {
                var kind = edge.Kind switch
                {
                    ModuleEdgeKind.Contract => "sözleşme",
                    ModuleEdgeKind.Event => "event",
                    _ => "**doğrudan referans (⚠ ihlal adayı)**",
                };

                // The first piece of evidence goes on the line: a dependency claim nobody can open
                // is a dependency claim nobody can check, and some module pages carry nothing else.
                var where = edge.Evidence.Count == 0 ? string.Empty : $"<br>  `{edge.Evidence[0]}`";

                page.AppendLine(CultureInfo.InvariantCulture,
                    $"- `{other(edge)}` — {kind}, {edge.Count} çağrı{where}");
            }
        }

        // The blank line is what ends the block. Without it a bold line following a bullet is lazy
        // continuation: markdown folds the next section INTO the last list item. It parses, and it
        // renders wrong - exactly the class of defect no syntax gate can see.
        page.AppendLine();
    }

    private static void AppendLimitations(StringBuilder page, IReadOnlyList<string> limitations)
    {
        page.AppendLine("## Bilinen sınırlar");
        page.AppendLine();

        if (limitations.Count == 0)
        {
            page.AppendLine("Bu sayfa için kaydedilmiş bir sınır yok.");
            return;
        }

        foreach (var limitation in limitations)
        {
            page.AppendLine(CultureInfo.InvariantCulture, $"- {limitation}");
        }
    }

    private static void Save(string root, string relative, string content, List<string> written)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // UTF-8 without BOM and \n line endings, so two runs on two machines agree byte for byte.
        File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(false));
        written.Add(relative.Replace('\\', '/'));
    }

    private static bool Matches(Node endpoint, string selector) =>
        string.Equals(endpoint.Id, selector, StringComparison.Ordinal)
        || string.Equals(endpoint.DisplayName, selector, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A file name from a route. Deterministic and reversible enough to recognise:
    /// "POST /api/ordering/checkout" becomes "post-api-ordering-checkout".
    /// </summary>
    public static string FileNameFor(string displayName)
    {
        var chars = displayName
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var name = new string(chars);

        while (name.Contains("--", StringComparison.Ordinal))
        {
            name = name.Replace("--", "-", StringComparison.Ordinal);
        }

        return name.Trim('-');
    }
}
