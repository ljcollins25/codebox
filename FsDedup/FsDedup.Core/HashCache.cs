using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace FsDedup;

/// <summary>Key of a cached hash: the file's identity, size and last-write time. Any change makes the entry a miss.</summary>
public readonly record struct CacheKey(ulong Volume, UInt128 FileId, long Size, long WriteTicks);

public sealed class CacheEntry
{
    public UInt128? Partial { get; set; }
    public UInt128? Full { get; set; }
}

/// <summary>
/// Hash cache, one JSON object per line: a header line {"fsdedup":"cache","version":1}, then one entry per file.
/// Written to a temp file and moved over the old one, so a crash leaves the previous cache intact. A cache that cannot
/// be read, or has another version, is ignored (the run just hashes again).
/// </summary>
public sealed class HashCache
{
    public const int Version = 1;
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> entries = new();

    private sealed record Header(string Fsdedup, int Version);
    private sealed record Line(string V, string I, long S, long W, string? P, string? F);

    public int Count => entries.Count;

    public CacheEntry? Get(CacheKey key) => entries.TryGetValue(key, out var e) ? e : null;

    public CacheEntry GetOrAdd(CacheKey key) => entries.GetOrAdd(key, _ => new CacheEntry());

    public static HashCache Load(string path)
    {
        var cache = new HashCache();
        try
        {
            if (!File.Exists(path)) return cache;
            using var reader = new StreamReader(path);
            var first = reader.ReadLine();
            if (first is null) return cache;
            var header = JsonSerializer.Deserialize<Header>(first, Json);
            if (header is null || header.Fsdedup != "cache" || header.Version != Version) return cache;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                try
                {
                    var l = JsonSerializer.Deserialize<Line>(line, Json);
                    if (l is null) continue;
                    var key = new CacheKey(ulong.Parse(l.V, NumberStyles.HexNumber), UInt128.Parse(l.I, NumberStyles.HexNumber), l.S, l.W);
                    cache.entries[key] = new CacheEntry
                    {
                        Partial = l.P is null ? null : UInt128.Parse(l.P, NumberStyles.HexNumber),
                        Full = l.F is null ? null : UInt128.Parse(l.F, NumberStyles.HexNumber),
                    };
                }
                catch (Exception ex) when (ex is JsonException or FormatException or OverflowException) { /* skip a bad line */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new HashCache();
        }
        return cache;
    }

    /// <summary>Writes the entries whose keys are in <paramref name="keep"/> (files seen in this run) atomically.</summary>
    public void Save(string path, ISet<CacheKey> keep)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs))
        {
            w.WriteLine(JsonSerializer.Serialize(new Header("cache", Version), Json));
            foreach (var (key, e) in entries)
            {
                if (!keep.Contains(key) || (e.Partial is null && e.Full is null)) continue;
                w.WriteLine(JsonSerializer.Serialize(new Line(key.Volume.ToString("x"), key.FileId.ToString("x"), key.Size,
                    key.WriteTicks, e.Partial?.ToString("x"), e.Full?.ToString("x")), Json));
            }
            w.Flush();
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
