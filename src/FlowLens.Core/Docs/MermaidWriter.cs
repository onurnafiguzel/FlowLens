using System.Globalization;
using System.Text;

namespace FlowLens.Core.Docs;

/// <summary>
/// Emits Mermaid that GitHub renders.
/// <para>
/// Two rules carry the whole file. <b>Node ids are generated</b> - n0, n1, ... - never derived from
/// a label: the measured labels contain <c>{</c>, <c>}</c>, <c>:</c> and <c>/</c>
/// (<c>/api/cart/items/{productId:guid}</c>), all of which are syntax errors in a Mermaid
/// identifier. <b>Every label is quoted</b>, which is what lets those characters through.
/// </para>
/// <para>
/// Ids are assigned in sorted-id order, so the same graph always emits the same text regardless of
/// the order it arrived in - the same discipline GraphJson.Canonical applies to graph.json.
/// </para>
/// </summary>
public static class MermaidWriter
{
    public static string Flow(FlowDiagram diagram) => $"```mermaid\n{FlowBody(diagram)}```\n";

    /// <summary>
    /// The diagram without the fences, newlines normalised to \n.
    /// <para>
    /// The normalisation is not cosmetic: this text is what the mermaid.live link compresses, and
    /// AppendLine writes Environment.NewLine. Left alone, the same graph would produce a different
    /// link on Windows and on Linux and every flow page would differ by one line between machines.
    /// </para>
    /// </summary>
    public static string FlowBody(FlowDiagram diagram)
    {
        var ids = Identifiers(diagram.Nodes.Select(n => n.Id));
        var builder = new StringBuilder();

        builder.AppendLine(CultureInfo.InvariantCulture, $"flowchart {Direction(diagram)}");

        // The module travels on the label, not in a box around the node. Subgraphs were the first
        // design and measurement rejected them: on checkout, 12 of 33 edges ran straight through a
        // box belonging to neither of their endpoints, so "an arrow leaving a box means a crossed
        // module boundary" was wrong 36% of the time. A label is right every time. Measured on the
        // same diagram, dropping the boxes also cut parallel bundling from 24 pairs to 11 and the
        // longest side-by-side run from 1008px to 606px (docs/phase-5-notes.md §9.1).
        foreach (var node in diagram.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  {ids[node.Id]}{Shape(node)}");
        }

        builder.AppendLine();

        foreach (var edge in diagram.Edges)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  {ids[edge.FromId]} {Arrow(edge)} {ids[edge.ToId]}");
        }

        var marked = diagram.Nodes.Where(n => n.HasDiagnostic).ToList();

        if (marked.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  classDef unseen stroke-dasharray: 4 4,stroke-width:2px");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  class {string.Join(',', marked.OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => ids[n.Id]))} unseen");
        }

        return builder.ToString().ReplaceLineEndings("\n");
    }

    public static string ModuleGraph(ModuleGraph graph)
    {
        var ids = Identifiers(graph.Modules);
        var builder = new StringBuilder();

        builder.AppendLine("```mermaid");
        builder.AppendLine("flowchart LR");

        foreach (var module in graph.Modules)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  {ids[module]}[\"{Escape(module)}\"]");
        }

        builder.AppendLine();

        foreach (var edge in graph.Edges)
        {
            var label = $"{edge.Kind switch
            {
                ModuleEdgeKind.Contract => "contract",
                ModuleEdgeKind.Event => "event",
                _ => "direct",
            }} x{edge.Count}";

            var arrow = edge.Kind switch
            {
                ModuleEdgeKind.Contract => $"-->|\"{label}\"|",
                ModuleEdgeKind.Event => $"-.->|\"{label}\"|",
                _ => $"==>|\"{label}\"|",
            };

            builder.AppendLine(CultureInfo.InvariantCulture, $"  {ids[edge.From]} {arrow} {ids[edge.To]}");
        }

        var violations = graph.Edges.Where(e => e.Kind == ModuleEdgeKind.Direct).ToList();

        if (violations.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  classDef flagged stroke-width:3px");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  class {string.Join(',', violations.Select(e => ids[e.To]).Distinct().Order(StringComparer.Ordinal))} flagged");
        }

        builder.AppendLine("```");

        return builder.ToString();
    }

    /// <summary>
    /// Top-down unless one node fans out too far.
    /// <para>
    /// Measured over all 25 endpoints, both directions, subgraphs off. Top-down is narrower for
    /// every diagram whose widest fan-out is 6 edges or fewer, and left-right is narrower for both
    /// diagrams above that (fan 9 and fan 17) - top-down turns a wide fan into a horizontal strip
    /// 2315px and 4420px across. The observed values leave a clean gap between 6 and 9, so the
    /// threshold sits in the middle of it.
    /// </para>
    /// <para>
    /// The FAN is the rule, not the node count: node counts overlap (a 12-node diagram wants
    /// top-down when its fan is 4 and left-right when its fan is 9), so a size threshold would
    /// have to guess where the fan threshold measures. See docs/phase-5-notes.md §9.4.
    /// </para>
    /// </summary>
    private const int WidestFanForTopDown = 7;

    private static string Direction(FlowDiagram diagram)
    {
        var fan = diagram.Edges
            .GroupBy(e => e.FromId, StringComparer.Ordinal)
            .Select(g => g.Count())
            .DefaultIfEmpty(0)
            .Max();

        return fan <= WidestFanForTopDown ? "TD" : "LR";
    }

    private static string Shape(DiagramNode node)
    {
        var label = Escape(node.Module.Length == 0 ? node.DisplayName : $"{node.Module} · {node.DisplayName}");

        return node.Kind switch
        {
            // Tables are cylinders, events are rounded, everything else is a box. Shape carries kind
            // so the label does not have to.
            NodeKind.Table => $"[(\"{label}\")]",
            NodeKind.Event => $"(\"{label}\")",
            NodeKind.ExternalCall => $">\"{label}\"]",
            NodeKind.Endpoint => $"[[\"{label}\"]]",
            _ => $"[\"{label}{(node.Ambiguous ? " (ambiguous)" : string.Empty)}\"]",
        };
    }

    private static string Arrow(DiagramEdge edge)
    {
        var head = edge.Kind switch
        {
            DiagramEdgeKind.Async => "-.->",
            DiagramEdgeKind.Data => "==>",
            _ => "-->",
        };

        return edge.Label.Length == 0
            ? head
            : $"{head}|\"{Escape(edge.Label)}\"|";
    }

    /// <summary>
    /// Deterministic n0..nN, assigned in sorted-id order. Two runs over the same graph emit the
    /// same identifiers whatever order the nodes arrived in.
    /// </summary>
    private static Dictionary<string, string> Identifiers(IEnumerable<string> ids)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var id in ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            map[id] = $"n{index++}";
        }

        return map;
    }

    /// <summary>
    /// Quoted labels carry <c>{</c>, <c>}</c>, <c>:</c> and <c>/</c> unharmed. The three that still
    /// need replacing are the quote itself and the angle brackets, which Mermaid renders as HTML.
    /// </summary>
    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
