using System.Diagnostics;

namespace FlowLens.Core;

/// <param name="ApproximateBytes">
/// Managed heap growth across the load, measured once with a forced collection. An estimate by
/// nature - the GC does not attribute - but a measured one rather than a guess.
/// </param>
public sealed record GraphSnapshot(
    GraphDocument Document,
    CodeGraph Graph,
    IReadOnlyList<string> Diagnostics,
    DateTime FileWrittenAtUtc,
    long FileLength,
    DateTime LoadedAtUtc,
    long ApproximateHeapBytes);

/// <summary>
/// Holds the loaded graph and keeps it fresh.
/// <para>
/// In Core because the API is not the only consumer: Phase 5's documentation generator and Phase 6's
/// triage both read graph.json, and all three need the same freshness and failure behaviour.
/// </para>
/// <para>
/// <b>A broken file never replaces a working one.</b> <c>flowlens build</c> writes with
/// File.WriteAllText, so a request can land mid-write and read a truncated document. Dropping to an
/// empty graph then would turn "the build is running" into "this flow touches nothing" - the exact
/// silent-wrong-answer shape this project exists to avoid. The last good snapshot is kept and the
/// failure is reported instead.
/// </para>
/// </summary>
public sealed class GraphSource(GraphPathResolution resolution)
{
    private readonly Lock _gate = new();
    private volatile GraphSnapshot? _snapshot;
    private volatile string? _loadError;
    private int _reloadCount;
    private bool _measured;

    public GraphSource(string path)
        : this(new GraphPathResolution(
            System.IO.Path.GetFullPath(path),
            File.Exists(System.IO.Path.GetFullPath(path)),
            [System.IO.Path.GetFullPath(path)],
            new Dictionary<string, string>(StringComparer.Ordinal)))
    {
    }

    public string Path { get; } = resolution.Path;

    /// <summary>
    /// Every path the search tried. Carried so a failure can list them all: naming one path that
    /// happened to be wrong leaves the reader guessing why it was that one.
    /// </summary>
    public IReadOnlyList<string> AttemptedPaths { get; } = resolution.Attempted;

    /// <summary>CurrentDirectory and BaseDirectory, the two values whose disagreement causes this.</summary>
    public IReadOnlyDictionary<string, string> PathDiagnostics { get; } = resolution.Diagnostics;

    private static string DefaultDescription => GraphPathResolver.DefaultFileName;

    /// <summary>Null only when no load has ever succeeded.</summary>
    public GraphSnapshot? Snapshot => _snapshot;

    /// <summary>Why the most recent load attempt failed, or null. Set even when a good snapshot survives.</summary>
    public string? LoadError => _loadError;

    public int ReloadCount => _reloadCount;

    /// <summary>
    /// Reloads if the file changed, then returns the current snapshot.
    /// <para>
    /// Freshness is decided by <b>write time AND length</b>, both read from one FileInfo. Timestamp
    /// alone is not enough: its resolution is filesystem-dependent (coarse on FAT and on some network
    /// shares), so two builds finishing inside the same tick would leave the second one invisible and
    /// the API would serve a stale graph believing it fresh. Length costs nothing extra and closes
    /// that class of miss. What remains - same tick AND same byte count with different content - is
    /// recorded in docs rather than hidden.
    /// </para>
    /// </summary>
    public GraphSnapshot? Refresh()
    {
        var info = new FileInfo(Path);

        if (!info.Exists)
        {
            _loadError =
                $"{DefaultDescription} bulunamadi. Denenen yollar: " +
                string.Join(" | ", AttemptedPaths) +
                ". API solution yuklemez; grafigi once 'flowlens build' uretir.";

            return _snapshot;
        }

        var current = _snapshot;

        if (current is not null
            && current.FileWrittenAtUtc == info.LastWriteTimeUtc
            && current.FileLength == info.Length)
        {
            return current;
        }

        lock (_gate)
        {
            // Re-check inside the lock: several requests can notice the same change at once, and
            // only one of them needs to pay for the reload.
            current = _snapshot;

            if (current is not null
                && current.FileWrittenAtUtc == info.LastWriteTimeUtc
                && current.FileLength == info.Length)
            {
                return current;
            }

            try
            {
                var loaded = Load(info);

                if (_snapshot is not null)
                {
                    Interlocked.Increment(ref _reloadCount);
                }

                _snapshot = loaded;
                _loadError = null;
                return loaded;
            }
            catch (Exception ex) when (ex is InvalidGraphException or System.Text.Json.JsonException or IOException)
            {
                _loadError = $"{Path} okunamadi: {ex.Message}";
                return _snapshot;
            }
        }
    }

    private GraphSnapshot Load(FileInfo info)
    {
        // Measured once, on the first successful load: forcing a full collection is far too
        // expensive to repeat per reload, and the number only has to be honest once.
        var measure = !_measured;
        var before = measure ? GC.GetTotalMemory(forceFullCollection: true) : 0;

        var document = GraphJson.Read(info.FullName);
        var graph = GraphJson.ToGraph(document);

        var after = measure ? GC.GetTotalMemory(forceFullCollection: true) : 0;
        _measured = true;

        return new GraphSnapshot(
            document,
            graph,
            document.Diagnostics,
            info.LastWriteTimeUtc,
            info.Length,
            DateTime.UtcNow,
            Math.Max(0, after - before));
    }

    /// <summary>
    /// Loads once and reports what it cost, for the measurement written into docs/phase-4-notes.md.
    /// Separate from <see cref="Refresh"/> so the numbers come from a clean process rather than
    /// from whatever a server happened to be doing.
    /// </summary>
    public static (long Bytes, TimeSpan Elapsed, int Nodes, int Edges) Measure(string path)
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var stopwatch = Stopwatch.StartNew();

        var document = GraphJson.Read(path);
        var graph = GraphJson.ToGraph(document);

        stopwatch.Stop();
        var after = GC.GetTotalMemory(forceFullCollection: true);

        // Touch the graph so nothing above can be optimised away before the second reading.
        return (after - before, stopwatch.Elapsed, graph.Nodes.Count, graph.Edges.Count);
    }
}
