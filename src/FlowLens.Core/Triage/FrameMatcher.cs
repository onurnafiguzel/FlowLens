namespace FlowLens.Core.Triage;

/// <summary>
/// What the graph could say about one frame. Four outcomes, and the difference between the middle
/// two is the point: "FlowLens cannot see this frame" is not "this call does not exist".
/// </summary>
public enum FrameVerdict
{
    /// <summary>Exactly one node. </summary>
    Matched,

    /// <summary>
    /// A project frame with several candidate nodes and nothing in the trace to separate them.
    /// Measured: one such pair exists in this target, and it is genuinely unresolvable - the
    /// runtime renders both <c>Consume(ConsumeContext&lt;ProductCreated&gt;)</c> and
    /// <c>Consume(ConsumeContext&lt;ProductUpdated&gt;)</c> as <c>Consume(ConsumeContext`1 context)</c>.
    /// All candidates are listed; none is picked.
    /// </summary>
    Ambiguous,

    /// <summary>
    /// A project namespace, but no node. Common rather than exotic: measured 147 of the target's
    /// 300 source files have no node at all (validators, DTOs, value objects). The report must say
    /// this out loud - a silently missing frame reads as "there is nothing here".
    /// </summary>
    NotInGraph,

    /// <summary>Framework or third-party. Counted, not listed individually.</summary>
    Foreign,
}

/// <param name="Candidates">Every node the key resolved to, ordered by id. One when Matched.</param>
public sealed record MatchedFrame(
    StackFrame Frame,
    FrameVerdict Verdict,
    IReadOnlyList<Node> Candidates)
{
    public Node? Node => Verdict == FrameVerdict.Matched ? Candidates[0] : null;
}

/// <summary>
/// Resolves a stack frame to a graph node.
/// <para>
/// The key is <c>Namespace.Type.Method</c> with parameters removed, which step 0's measurement
/// showed is enough: 255 method-like nodes collapse to 254 distinct keys. Parameters are used only
/// to break the one collision, and they need normalising in both directions because a node id
/// spells <c>float[], int, System.Threading.CancellationToken</c> while the runtime spells
/// <c>Single[], Int32, CancellationToken</c>.
/// </para>
/// <para>
/// Nothing is chosen by enumeration order. When two candidates survive, the frame is reported
/// Ambiguous rather than resolved to whichever the dictionary happened to yield - Phase 5's lesson
/// that discovery itself has to be deterministic, not just the sort at the end.
/// </para>
/// </summary>
public sealed class FrameMatcher
{
    /// <summary>C# alias to CLR name. Only the aliases that can appear in a node id.</summary>
    private static readonly Dictionary<string, string> ClrNames = new(StringComparer.Ordinal)
    {
        ["bool"] = "Boolean",
        ["byte"] = "Byte",
        ["sbyte"] = "SByte",
        ["char"] = "Char",
        ["decimal"] = "Decimal",
        ["double"] = "Double",
        ["float"] = "Single",
        ["int"] = "Int32",
        ["uint"] = "UInt32",
        ["long"] = "Int64",
        ["ulong"] = "UInt64",
        ["short"] = "Int16",
        ["ushort"] = "UInt16",
        ["object"] = "Object",
        ["string"] = "String",
        ["nint"] = "IntPtr",
        ["nuint"] = "UIntPtr",
    };

    private static readonly NodeKind[] MethodKinds =
        [NodeKind.Method, NodeKind.Handler, NodeKind.Repository];

    private readonly Dictionary<string, List<Node>> _byKey;

    public FrameMatcher(CodeGraph graph)
    {
        _byKey = new Dictionary<string, List<Node>>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes.Where(n => MethodKinds.Contains(n.Kind)))
        {
            var key = KeyOf(node.Id);

            if (!_byKey.TryGetValue(key, out var bucket))
            {
                bucket = [];
                _byKey[key] = bucket;
            }

            bucket.Add(node);
        }

        // A total order, applied once here, so no caller can be affected by graph.Nodes order.
        foreach (var bucket in _byKey.Values)
        {
            bucket.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        }

        // Derived from the graph, never hardcoded (roadmap rule 7). This target yields exactly
        // one prefix, "ModularCommerce", but the rule is what is written down, not the value.
        ProjectPrefixes =
        [
            .. graph.Nodes
                .Select(n => n.Id)
                .Where(id => !id.Contains(':', StringComparison.Ordinal))
                .Select(FirstSegment)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>Top-level namespace segments the graph knows about. What separates a project frame from a foreign one.</summary>
    public IReadOnlyList<string> ProjectPrefixes { get; }

    public MatchedFrame Match(StackFrame frame)
    {
        if (!IsProjectFrame(frame))
        {
            return new MatchedFrame(frame, FrameVerdict.Foreign, []);
        }

        if (!_byKey.TryGetValue(frame.Key, out var candidates))
        {
            return new MatchedFrame(frame, FrameVerdict.NotInGraph, []);
        }

        if (candidates.Count == 1)
        {
            return new MatchedFrame(frame, FrameVerdict.Matched, candidates);
        }

        var narrowed = candidates.Where(node => SignatureFits(node.Id, frame)).ToList();

        return narrowed.Count == 1
            ? new MatchedFrame(frame, FrameVerdict.Matched, narrowed)
            : new MatchedFrame(frame, FrameVerdict.Ambiguous, narrowed.Count > 0 ? narrowed : candidates);
    }

    public bool IsProjectFrame(StackFrame frame) =>
        ProjectPrefixes.Contains(FirstSegment(frame.TypeName), StringComparer.Ordinal);

    /// <summary>The node-id form of a frame key: everything before the parameter list.</summary>
    private static string KeyOf(string nodeId)
    {
        var open = nodeId.IndexOf('(');
        return open < 0 ? nodeId : nodeId[..open];
    }

    private static string FirstSegment(string value)
    {
        var dot = value.IndexOf('.');
        return dot < 0 ? value : value[..dot];
    }

    /// <summary>
    /// Whether a node's parameter list can be the one the runtime rendered. Arity first, then
    /// short names. Deliberately permissive on generics: the runtime erases type arguments
    /// (<c>ConsumeContext`1</c>), so demanding they match would reject the correct node instead of
    /// admitting the ambiguity.
    /// </summary>
    private static bool SignatureFits(string nodeId, StackFrame frame)
    {
        var open = nodeId.IndexOf('(');

        if (open < 0)
        {
            return frame.ParameterTypes.Count == 0;
        }

        var declared = SplitParameters(nodeId[(open + 1)..^1]);

        return declared.Count == frame.ParameterTypes.Count
            && declared.Zip(frame.ParameterTypes).All(pair => Comparable(pair.First, pair.Second));
    }

    private static bool Comparable(string declared, string rendered) =>
        string.Equals(Normalize(declared), Normalize(rendered), StringComparison.Ordinal);

    /// <summary>
    /// Reduces both spellings to one: unqualified, CLR-named, without generic arguments or arity.
    /// <c>System.Threading.CancellationToken</c> and <c>CancellationToken</c> meet here, as do
    /// <c>float[]</c> and <c>Single[]</c>, and <c>ConsumeContext&lt;X&gt;</c> and <c>ConsumeContext`1</c>.
    /// </summary>
    private static string Normalize(string type)
    {
        var text = type.Trim();

        var suffix = string.Empty;

        while (text.EndsWith("[]", StringComparison.Ordinal))
        {
            suffix += "[]";
            text = text[..^2];
        }

        var generic = text.IndexOf('<');
        if (generic > 0)
        {
            text = text[..generic];
        }

        var tick = text.IndexOf('`');
        if (tick > 0)
        {
            text = text[..tick];
        }

        var dot = text.LastIndexOf('.');
        if (dot >= 0)
        {
            text = text[(dot + 1)..];
        }

        return (ClrNames.GetValueOrDefault(text, text)) + suffix;
    }

    private static List<string> SplitParameters(string text)
    {
        var parameters = new List<string>();

        if (text.Trim().Length == 0)
        {
            return parameters;
        }

        var depth = 0;
        var start = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || (text[i] == ',' && depth == 0))
            {
                parameters.Add(text[start..i].Trim());
                start = i + 1;
                continue;
            }

            depth += text[i] switch
            {
                '<' or '[' => 1,
                '>' or ']' => -1,
                _ => 0,
            };
        }

        return parameters;
    }
}
