namespace ToolRun;

/// <summary>
/// Moves that survive a Windows virus scanner or indexer holding freshly written files: retried with a growing pause on UnauthorizedAccessException / IOException;
/// a target that appeared meanwhile (another toolrun won the race) counts as done when it is complete; after the last try a directory is copied instead.
/// </summary>
public static class SafeMove
{
    public const int Tries = 10;

    /// <summary>Tests: replace the real directory move.</summary>
    internal static readonly AsyncLocal<Action<string, string>?> DirMover = new();
    /// <summary>Tests: replace the pause between tries.</summary>
    internal static readonly AsyncLocal<Action<TimeSpan>?> Sleeper = new();
    /// <summary>Tests: the pause step (default 100 ms: pauses of 100, 200 ... 900 ms, about 4.5 s in all).</summary>
    internal static readonly AsyncLocal<int?> StepMs = new();

    private static void Pause(int attempt)
    {
        var t = TimeSpan.FromMilliseconds((StepMs.Value ?? 100) * (attempt + 1));
        if (Sleeper.Value is { } s) s(t); else Thread.Sleep(t);
    }

    private static bool Transient(Exception ex) => ex is UnauthorizedAccessException or IOException;

    /// <summary>Moves a folder into place. isComplete says whether an existing target is a finished install (its version stamp matches); then the source is simply dropped.</summary>
    public static void Dir(string source, string target, Func<bool> isComplete)
    {
        Exception? last = null;
        for (int i = 0; i < Tries; i++)
        {
            if (Directory.Exists(target) && isComplete()) { Discard(source); return; }
            try
            {
                if (DirMover.Value is { } m) m(source, target); else Directory.Move(source, target);
                return;
            }
            catch (Exception ex) when (Transient(ex))
            {
                last = ex;
                if (Directory.Exists(target) && isComplete()) { Discard(source); return; }
                if (i < Tries - 1) Pause(i);
            }
        }
        // still locked: copying only needs read access to the files
        try
        {
            if (Directory.Exists(target)) { if (isComplete()) { Discard(source); return; } ToolRunHome.DeleteDir(target); }
            Copy(source, target);
            Discard(source);
        }
        catch (Exception ex) when (Transient(ex))
        {
            try { ToolRunHome.DeleteDir(target); } catch (Exception e) when (Transient(e)) { }
            throw new IOException($"Could not move '{source}' to '{target}' after {Tries} tries ({last?.Message}); something may be holding the files (antivirus, indexer).", last);
        }
    }

    /// <summary>Moves a file (replacing the target), retried the same way.</summary>
    public static void File(string source, string target)
    {
        Exception? last = null;
        for (int i = 0; i < Tries; i++)
        {
            try { System.IO.File.Move(source, target, true); return; }
            catch (Exception ex) when (Transient(ex))
            {
                last = ex;
                if (i < Tries - 1) Pause(i);
            }
        }
        throw new IOException($"Could not move '{source}' to '{target}' after {Tries} tries ({last?.Message}).", last);
    }

    /// <summary>Deletes a folder, quietly: a scanner holding a file must not fail the run that has already succeeded.</summary>
    public static void DiscardQuietly(string dir) => Discard(dir);

    private static void Discard(string dir)
    {
        try { if (Directory.Exists(dir)) ToolRunHome.DeleteDir(dir); }
        catch (Exception ex) when (Transient(ex)) { }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) System.IO.File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
