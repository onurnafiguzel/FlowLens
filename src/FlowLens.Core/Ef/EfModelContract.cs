using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowLens.Core.Ef;

/// <summary>
/// The wire format for <see cref="EfModelSnapshot"/>.
/// <para>
/// Nothing in FlowLens serialises the model today - <see cref="EfProbe"/> hands the records
/// straight to <see cref="EfModelIndex"/> in the same process. This exists anyway, and is covered
/// by a round-trip test, because it is the thing that keeps the out-of-process escape hatch cheap:
/// as long as the contract provably survives JSON, moving the read into a separate
/// <c>FlowLens.EfProbe</c> executable is a change to one class rather than to the whole data layer.
/// </para>
/// <para>
/// If that migration ever happens, the probe writes <see cref="Serialize"/>'s output to stdout and
/// FlowLens calls <see cref="Deserialize"/> on it. The version tag is here so a stale probe binary
/// is rejected rather than silently misread.
/// </para>
/// </summary>
public static class EfModelContract
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(IReadOnlyList<EfModelSnapshot> snapshots) =>
        JsonSerializer.Serialize(new EfModelEnvelope(Version, snapshots), Options);

    public static IReadOnlyList<EfModelSnapshot> Deserialize(string json)
    {
        var envelope = JsonSerializer.Deserialize<EfModelEnvelope>(json, Options)
            ?? throw new InvalidOperationException("EF model payload was empty.");

        if (envelope.Version != Version)
        {
            throw new InvalidOperationException(
                $"EF model payload is contract version {envelope.Version}; this build reads {Version}.");
        }

        return envelope.Snapshots;
    }

    private sealed record EfModelEnvelope(int Version, IReadOnlyList<EfModelSnapshot> Snapshots);
}
