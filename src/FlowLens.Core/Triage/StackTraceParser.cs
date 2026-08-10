using System.Text.RegularExpressions;

namespace FlowLens.Core.Triage;

/// <summary>What a line of a stack trace turned out to be. Every line gets one - nothing is dropped.</summary>
public enum LineKind
{
    /// <summary>An <c>at ...</c> line that parsed into a frame.</summary>
    Frame,

    /// <summary>An <c>at ...</c> line that did NOT parse. Reported in full, never swallowed.</summary>
    Unparsed,

    /// <summary><c>--- End of stack trace from previous location ---</c> and friends.</summary>
    Separator,

    /// <summary>The exception header, its message, or the trailing "Exception data:" block.</summary>
    Text,
}

/// <param name="Method">Method name after demangling - the name a developer would search for.</param>
/// <param name="Key">
/// <c>Namespace.Type.Method</c>, parameters removed. Measured on this target: 254 distinct keys
/// over 255 method-like nodes, so the key alone identifies all but one pair of overloads.
/// </param>
/// <param name="ParameterTypes">
/// Short CLR type names as the runtime renders them - <c>Int32</c>, <c>Guid</c>, <c>Single[]</c>.
/// Measured in step 0a: NOT the C# aliases and NOT namespace-qualified, which is why matching a
/// node id (<c>float[], int, System.Threading.CancellationToken</c>) needs the alias table in
/// <see cref="FrameMatcher"/>.
/// </param>
/// <param name="FilePath">Absolute path recorded in the PDB, or empty when the trace carries none.</param>
public sealed record StackFrame(
    int Index,
    string Raw,
    string TypeName,
    string Method,
    string Key,
    IReadOnlyList<string> ParameterTypes,
    string FilePath,
    int Line)
{
    public bool HasLocation => FilePath.Length > 0 && Line > 0;

    public string Location => HasLocation ? $"{FilePath}:{Line}" : FilePath;
}

/// <param name="Kind">Never null and never absent: every input line is classified.</param>
public sealed record TraceLine(int Number, string Text, LineKind Kind, StackFrame? Frame);

/// <param name="ExceptionType">Fully qualified, as written on the header line.</param>
/// <param name="Lines">Every line of the input, classified. The parser's honesty is checkable from this.</param>
public sealed record ParsedTrace(
    string ExceptionType,
    string Message,
    IReadOnlyList<StackFrame> Frames,
    IReadOnlyList<TraceLine> Lines)
{
    public IReadOnlyList<TraceLine> Unparsed =>
        [.. Lines.Where(l => l.Kind == LineKind.Unparsed)];

    public int Count(LineKind kind) => Lines.Count(l => l.Kind == kind);
}

/// <summary>
/// Turns the text of a .NET stack trace into frames.
/// <para>
/// <b>Written against measured output, not remembered output.</b> Step 0a ran a .NET 10 program
/// and captured what <c>Exception.ToString()</c> actually emits; two of the rules here exist
/// because that measurement contradicted the assumption. Async METHODS come out demangled
/// (<c>HandleAsync</c>, not <c>&lt;HandleAsync&gt;d__4.MoveNext()</c>) - but async LAMBDAS do not
/// (<c>&lt;&gt;c.&lt;&lt;RunAsync&gt;b__0_0&gt;d.MoveNext()</c>), so the MoveNext handling is
/// still load-bearing. And parameter types render as CLR short names, not C# aliases.
/// </para>
/// <para>
/// <b>Nothing is silently dropped.</b> Roadmap rule 8. Every input line is classified and counted;
/// an <c>at</c> line that fails to parse becomes <see cref="LineKind.Unparsed"/> and is reported
/// verbatim rather than quietly skipped, because a parser that hides what it could not read is
/// indistinguishable from one that read everything.
/// </para>
/// </summary>
public static partial class StackTraceParser
{
    public static ParsedTrace Parse(string text)
    {
        var lines = new List<TraceLine>();
        var frames = new List<StackFrame>();
        var header = string.Empty;

        var number = 0;

        foreach (var raw in (text ?? string.Empty).ReplaceLineEndings("\n").Split('\n'))
        {
            number++;
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                lines.Add(new TraceLine(number, raw, LineKind.Text, null));
                continue;
            }

            if (SeparatorLine().IsMatch(trimmed))
            {
                lines.Add(new TraceLine(number, raw, LineKind.Separator, null));
                continue;
            }

            if (FrameLine().Match(trimmed) is { Success: true } match)
            {
                var frame = Frame(frames.Count, trimmed, match);

                if (frame is null)
                {
                    lines.Add(new TraceLine(number, raw, LineKind.Unparsed, null));
                    continue;
                }

                frames.Add(frame);
                lines.Add(new TraceLine(number, raw, LineKind.Frame, frame));
                continue;
            }

            if (header.Length == 0 && frames.Count == 0 && LooksLikeHeader(trimmed))
            {
                header = trimmed;
            }

            lines.Add(new TraceLine(number, raw, LineKind.Text, null));
        }

        var (type, message) = Header(header);

        return new ParsedTrace(type, message, frames, lines);
    }

    /// <summary>
    /// Whether a line can be the exception header: a dotted, space-free type followed by ": ".
    /// <para>
    /// Without this test the FIRST text line wins, and in practice the first line is often not the
    /// header - an incident is usually pasted with a log prefix, a timestamp, or a comment above
    /// it. Taking whatever came first would then report a timestamp as the exception type: not a
    /// crash, just a confidently wrong field, which is the failure shape this project keeps
    /// finding.
    /// </para>
    /// </summary>
    private static bool LooksLikeHeader(string line)
    {
        var split = line.IndexOf(": ", StringComparison.Ordinal);

        if (split <= 0)
        {
            return false;
        }

        var type = line[..split];
        var hresult = type.IndexOf(" (0x", StringComparison.Ordinal);

        if (hresult > 0)
        {
            type = type[..hresult];
        }

        return type.Contains('.', StringComparison.Ordinal)
            && !type.Any(char.IsWhiteSpace);
    }

    /// <summary>
    /// Splits "Npgsql.PostgresException (0x80004005): 42P01: relation ... does not exist" into a
    /// type and a message. The HRESULT in parentheses is dropped - it identifies the exception
    /// class, which the type name already does, and keeping it would break equality against a
    /// second capture of the same failure.
    /// </summary>
    private static (string Type, string Message) Header(string line)
    {
        if (line.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        var split = line.IndexOf(": ", StringComparison.Ordinal);

        var type = split < 0 ? line : line[..split];
        var message = split < 0 ? string.Empty : line[(split + 2)..];

        var hresult = type.IndexOf(" (0x", StringComparison.Ordinal);

        return (hresult > 0 ? type[..hresult] : type, message);
    }

    private static StackFrame? Frame(int index, string raw, Match match)
    {
        var signature = match.Groups["sig"].Value.Trim();
        var open = OpeningParenthesis(signature);

        if (open < 0 || !signature.EndsWith(')'))
        {
            return null;
        }

        var qualified = Demangle(signature[..open]);

        if (qualified is not var (typeName, method) || method.Length == 0)
        {
            return null;
        }

        var parameters = Parameters(signature[(open + 1)..^1]);

        var line = int.TryParse(match.Groups["line"].Value, out var parsed) ? parsed : 0;

        return new StackFrame(
            index,
            raw,
            typeName,
            method,
            typeName.Length == 0 ? method : $"{typeName}.{method}",
            parameters,
            match.Groups["file"].Value.Trim(),
            line);
    }

    /// <summary>
    /// The parenthesis that opens the PARAMETER list - the last one at nesting depth zero.
    /// Scanning from the left would stop inside a generic argument such as
    /// <c>ExecuteAsync[TState,TResult](TState state, Func`4 operation)</c>.
    /// </summary>
    private static int OpeningParenthesis(string signature)
    {
        var depth = 0;

        for (var i = signature.Length - 1; i >= 0; i--)
        {
            switch (signature[i])
            {
                case ')':
                    depth++;
                    break;
                case '(':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// Parameter TYPES only, names discarded. Generic arguments can contain commas
    /// (<c>ConsumeContext&lt;A, B&gt;</c>), so splitting tracks bracket depth.
    /// </summary>
    private static IReadOnlyList<string> Parameters(string text)
    {
        if (text.Trim().Length == 0)
        {
            return [];
        }

        var types = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || (text[i] == ',' && depth == 0))
            {
                types.Add(TypeOnly(text[start..i]));
                start = i + 1;
                continue;
            }

            depth += text[i] switch
            {
                '<' or '[' or '(' => 1,
                '>' or ']' or ')' => -1,
                _ => 0,
            };
        }

        return types;

        // "Guid productId" -> "Guid"; "Func`4 operation" -> "Func`4"; "ref Int32 x" -> "Int32".
        static string TypeOnly(string parameter)
        {
            var parts = parameter.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return parts.Length switch
            {
                0 => string.Empty,
                1 => parts[0],
                _ => parts[^2],
            };
        }
    }

    /// <summary>
    /// Turns a rendered method path into (type, method), undoing every compiler-generated wrapper
    /// measured in step 0a.
    /// <para>
    /// <c>Step0.LambdaAndGeneric.&lt;&gt;c.&lt;&lt;RunAsync&gt;b__0_0&gt;d.MoveNext</c>
    /// becomes <c>(Step0.LambdaAndGeneric, RunAsync)</c>. Resolving a lambda to its ENCLOSING
    /// method is the right answer rather than a convenience: the call graph walks a method's whole
    /// syntax subtree, so calls written inside a lambda already belong to the enclosing method
    /// (known-limitations L12).
    /// </para>
    /// </summary>
    private static (string Type, string Method)? Demangle(string path)
    {
        var segments = Split(StripGenericArguments(path));

        if (segments.Count == 0)
        {
            return null;
        }

        var method = segments[^1];
        segments.RemoveAt(segments.Count - 1);

        // The METHOD segment itself can be compiler-generated even when nothing follows it:
        // a local function renders as Program.<>c__DisplayClass0_0.<<Main>$>g__CaptureC|2, whose
        // last segment is the local function, not MoveNext. Measured in a real capture.
        if (Generated(method) && EnclosingName(method) is { Length: > 0 } declared)
        {
            method = declared;
        }

        while (segments.Count > 0 && Generated(segments[^1]))
        {
            var segment = segments[^1];
            segments.RemoveAt(segments.Count - 1);

            if (EnclosingName(segment) is { Length: > 0 } enclosing)
            {
                method = enclosing;
            }
        }

        return (string.Join('.', segments.Select(StripArity)), StripArity(method));
    }

    /// <summary>Splits on dots that are not inside angle brackets - <c>&lt;Main&gt;$</c> has none, but a nested generic could.</summary>
    private static List<string> Split(string path)
    {
        var segments = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i <= path.Length; i++)
        {
            if (i == path.Length || (path[i] == '.' && depth == 0))
            {
                if (i > start)
                {
                    segments.Add(path[start..i]);
                }

                start = i + 1;
                continue;
            }

            depth += path[i] switch
            {
                '<' => 1,
                '>' => -1,
                _ => 0,
            };
        }

        return segments;
    }

    /// <summary>Removes a generic method's argument list: <c>ExecuteAsync[TState,TResult]</c> -&gt; <c>ExecuteAsync</c>.</summary>
    private static string StripGenericArguments(string path)
    {
        var open = path.IndexOf('[');
        return open < 0 ? path : path[..open] + path[(path.LastIndexOf(']') + 1)..];
    }

    private static string StripArity(string segment)
    {
        var tick = segment.IndexOf('`');
        return tick < 0 ? segment : segment[..tick];
    }

    private static bool Generated(string segment) => segment.StartsWith('<');

    /// <summary>
    /// The user-written method a compiler-generated type belongs to.
    /// <c>&lt;HandleAsync&gt;d__4</c> -&gt; <c>HandleAsync</c>;
    /// <c>&lt;&lt;RunAsync&gt;b__0_0&gt;d</c> -&gt; <c>RunAsync</c>;
    /// <c>&lt;&gt;c</c> and <c>&lt;&gt;c__DisplayClass0_0</c> carry no name at all.
    /// </summary>
    private static string EnclosingName(string segment)
    {
        while (true)
        {
            if (!segment.StartsWith('<'))
            {
                return segment.TrimEnd('$');
            }

            var close = MatchingBracket(segment);

            if (close < 0)
            {
                return string.Empty;
            }

            var inner = segment[1..close];

            if (inner.Length == 0)
            {
                // "<>c", "<>c__DisplayClass0_0" - a closure holder, no method name inside.
                return string.Empty;
            }

            segment = inner;
        }
    }

    private static int MatchingBracket(string segment)
    {
        var depth = 0;

        for (var i = 0; i < segment.Length; i++)
        {
            switch (segment[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"^at\s+(?<sig>.+?)(?:\s+in\s+(?<file>.+?):line\s+(?<line>\d+))?$")]
    private static partial Regex FrameLine();

    [GeneratedRegex(@"^-{3}.*-{3}$")]
    private static partial Regex SeparatorLine();
}
