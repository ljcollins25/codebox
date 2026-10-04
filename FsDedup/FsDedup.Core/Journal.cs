using System.Text.Json;

namespace FsDedup;

/// <summary>
/// Write-ahead journal of replacements, one JSON line per step, flushed to disk before the step it announces:
/// begin (names of temp and backup), swapping (about to call ReplaceFile), swapped (it returned), end.
/// </summary>
public sealed class Journal : IDisposable
{
    public const string FileName = ".fsdedup-journal.jsonl";

    public sealed record Record(string Op, string Id, string? D = null, string? T = null, string? B = null, string? Note = null, bool HardLink = false, string? Original = null, long? OriginalCreation = null);

    private readonly FileStream stream;
    private readonly object gate = new();

    public Journal(string root)
    {
        Path = System.IO.Path.Combine(root, FileName);
        stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
    }

    public string Path { get; }

    public void Begin(string id, string dup, string temp, string backup, bool hardLink = false, string? original = null, long? originalCreation = null) => Write(new Record("begin", id, dup, temp, backup, HardLink: hardLink, Original: original, OriginalCreation: originalCreation));
    public void Swapping(string id) => Write(new Record("swapping", id));
    public void Swapped(string id) => Write(new Record("swapped", id));
    public void End(string id, string note) => Write(new Record("end", id, Note: note));

    public static void Append(string path, Record r)
    {
        using var s = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r) + "\n");
        s.Write(bytes);
        s.Flush(true);
    }

    private void Write(Record r)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r) + "\n");
        lock (gate)
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>
/// Cleans up after an interrupted run. For every operation that has no "end": if the swap was not confirmed and a
/// backup exists, the backup (the original file) is moved back over the destination; if the swap was confirmed the
/// backup is deleted. The temporary file is deleted in both cases. A destination is therefore either its old self or
/// the verified clone, never missing.
/// </summary>
public static class JournalRecovery
{
    /// <summary>Number of unfinished operations in the journal at <paramref name="root"/> (no changes made).</summary>
    public static int CountPending(string root) => ReadPending(System.IO.Path.Combine(root, Journal.FileName)).Count;

    /// <summary>Recovers and then deletes the journal. Returns what was done, one line per action.</summary>
    public static List<string> Run(string root, List<string> errors)
    {
        var done = new List<string>();
        var path = System.IO.Path.Combine(root, Journal.FileName);
        if (!File.Exists(path)) return done;
        bool clean = true;
        foreach (var op in ReadPending(path))
        {
            try
            {
                bool swapped = op.Swapped;
                if (op.HardLink && op.Original is not null && op.OriginalCreation is { } creation && File.Exists(op.Original))
                    File.SetCreationTimeUtc(op.Original, DateTime.FromFileTimeUtc(creation));
                if (op.B is not null && File.Exists(op.B))
                {
                    if (swapped && !op.HardLink)
                    {
                        File.Delete(op.B);
                        done.Add($"deleted leftover backup {op.B} (swap had completed)");
                    }
                    else if (op.D is not null)
                    {
                        File.Move(op.B, op.D, overwrite: true);
                        done.Add($"restored {op.D} from backup {op.B}");
                    }
                }
                if (op.T is not null && File.Exists(op.T))
                {
                    File.Delete(op.T);
                    done.Add($"deleted leftover temp file {op.T}");
                }
                if (op.B is null && op.T is null) done.Add($"nothing to clean for {op.D}");
            }
            catch (Exception ex)
            {
                clean = false;
                errors.Add($"recovery of {op.D} failed: {ex.Message}");
            }
        }
        if (clean) File.Delete(path);
        return done;
    }

    private sealed class Pending { public string? D, T, B, Original; public long? OriginalCreation; public bool Swapped, HardLink; }

    private static List<Pending> ReadPending(string path)
    {
        var ops = new Dictionary<string, Pending>();
        var order = new List<string>();
        if (!File.Exists(path)) return new List<Pending>();
        foreach (var line in File.ReadLines(path))
        {
            Journal.Record? r;
            try { r = JsonSerializer.Deserialize<Journal.Record>(line); }
            catch (JsonException) { continue; } // a torn last line
            if (r is null) continue;
            switch (r.Op)
            {
                case "begin": ops[r.Id] = new Pending { D = r.D, T = r.T, B = r.B, HardLink = r.HardLink, Original = r.Original, OriginalCreation = r.OriginalCreation }; order.Add(r.Id); break;
                case "swapped": if (ops.TryGetValue(r.Id, out var s)) s.Swapped = true; break;
                case "end": ops.Remove(r.Id); break;
            }
        }
        return order.Where(ops.ContainsKey).Select(i => ops[i]).ToList();
    }
}
