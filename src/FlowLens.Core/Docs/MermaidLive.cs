using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowLens.Core.Docs;

/// <summary>
/// Builds a mermaid.live URL for a diagram. The site carries its whole state in the fragment as
/// <c>base64url(zlib(json))</c>, so the link needs no server and no package.
/// <para>
/// <b>The compression is written out by hand, as stored blocks.</b> A general-purpose deflater
/// would also produce a valid stream, but not a REPRODUCIBLE one: the bytes it emits depend on the
/// compressor build, so the same graph could yield two different links on two machines and every
/// flow page would show a spurious diff. Stored blocks are fully determined by RFC 1950/1951 -
/// there is no encoder choice left to make - which is what keeps the byte-identical guarantee
/// true across runtimes, not just across two runs of one process. The cost is URL length; measured
/// in docs/phase-5-notes.md §9.6.
/// </para>
/// </summary>
public static class MermaidLive
{
    private const int MaxStoredBlock = 65535;

    private sealed record State(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("mermaid")] string Mermaid,
        [property: JsonPropertyName("autoSync")] bool AutoSync,
        [property: JsonPropertyName("updateDiagram")] bool UpdateDiagram);

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="diagramBody">The Mermaid source WITHOUT the fences, newlines already \n.</param>
    public static string UrlFor(string diagramBody)
    {
        var state = new State(diagramBody, "{\n  \"theme\": \"default\"\n}", true, true);
        var json = JsonSerializer.Serialize(state, Options);

        return "https://mermaid.live/edit#pako:" + Base64Url(ZLib(Encoding.UTF8.GetBytes(json)));
    }

    private static byte[] ZLib(byte[] data)
    {
        var output = new List<byte>(data.Length + 64) { 0x78, 0x01 };

        var offset = 0;

        do
        {
            var length = Math.Min(MaxStoredBlock, data.Length - offset);
            var last = offset + length >= data.Length;

            // BFINAL in bit 0, BTYPE 00 (stored) in bits 1-2; the rest of the byte is padding
            // because a stored block starts on a byte boundary.
            output.Add(last ? (byte)1 : (byte)0);
            output.Add((byte)(length & 0xFF));
            output.Add((byte)((length >> 8) & 0xFF));
            output.Add((byte)(~length & 0xFF));
            output.Add((byte)((~length >> 8) & 0xFF));
            output.AddRange(data.AsSpan(offset, length).ToArray());

            offset += length;
        }
        while (offset < data.Length);

        var adler = Adler32(data);
        output.Add((byte)(adler >> 24));
        output.Add((byte)(adler >> 16));
        output.Add((byte)(adler >> 8));
        output.Add((byte)adler);

        return [.. output];
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;

        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data)
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');
}
