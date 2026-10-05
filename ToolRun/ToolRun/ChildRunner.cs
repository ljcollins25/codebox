using System.Diagnostics;
using System.Runtime.InteropServices;
using Tool2App;

namespace ToolRun;

public sealed class LaunchPlan
{
    public required string FileName { get; init; }
    public List<string> Args { get; init; } = new();
    public Dictionary<string, string> Env { get; init; } = new();
    public string ToolId { get; init; } = "";
    public string ToolVersion { get; init; } = "";
    public string ToolDir { get; init; } = "";
    public string RuntimeRoot { get; init; } = "";
    public bool SystemRuntime { get; init; }
    public bool UsesApphost { get; init; }
    public string ModeLine { get; init; } = "";
    public List<string> Notes { get; init; } = new();

    /// <summary>The command line as a shell would need it, with the environment it adds.</summary>
    public string Describe()
    {
        var win = OperatingSystem.IsWindows();
        var parts = new List<string>();
        foreach (var (k, v) in Env) parts.Add(win ? $"set \"{k}={v}\" &&" : $"{k}={Quote(v)}");
        parts.Add(Quote(FileName));
        parts.AddRange(Args.Select(Quote));
        return string.Join(' ', parts);
    }

    private static string Quote(string s)
    {
        if (s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || "-_./:=@+,\\".Contains(c))) return s;
        return OperatingSystem.IsWindows() ? "\"" + s.Replace("\"", "\\\"") + "\"" : "'" + s.Replace("'", "'\\''") + "'";
    }
}

public static class Signals
{
    public const int SIGHUP = 1, SIGINT = 2, SIGQUIT = 3, SIGTERM = 15;

    [DllImport("toolrun-libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private static bool _resolver;
    private static void EnsureResolver()
    {
        if (_resolver) return;
        _resolver = true;
        NativeLibrary.SetDllImportResolver(typeof(Signals).Assembly, (name, asm, path) =>
        {
            if (name != "toolrun-libc") return IntPtr.Zero;
            foreach (var n in new[] { "libc.so.6", "libc", "libSystem.dylib", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1" })
                if (NativeLibrary.TryLoad(n, out var h)) return h;
            return IntPtr.Zero;
        });
    }

    /// <summary>Sends a POSIX signal to a process. Returns false where that is not possible (Windows).</summary>
    public static bool Send(int pid, int signal)
    {
        if (OperatingSystem.IsWindows()) return false;
        EnsureResolver();
        try { return kill(pid, signal) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}

/// <summary>Runs the child with the same stdin/stdout/stderr, working directory and arguments, and returns its exit code. Termination signals sent to toolrun are passed on.</summary>
public static class ChildRunner
{
    public static int Run(LaunchPlan plan)
    {
        var psi = new ProcessStartInfo(plan.FileName) { UseShellExecute = false };
        foreach (var a in plan.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in plan.Env) psi.Environment[k] = v;

        Process p;
        // Ctrl+C reaches the whole foreground group, so the child gets it from the terminal itself: we only have to stay alive until it is done.
        ConsoleCancelEventHandler cancel = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += cancel;
        var regs = new List<PosixSignalRegistration>();
        try
        {
            try { p = Process.Start(psi) ?? throw new ToolException("Could not start " + plan.FileName); }
            catch (System.ComponentModel.Win32Exception ex) { throw new ToolException($"Could not start {plan.FileName}: {ex.Message}", ex); }

            if (!OperatingSystem.IsWindows())
            {
                bool tty = !Console.IsInputRedirected;   // on a terminal SIGINT is already delivered to the child directly
                void Forward(PosixSignal s, int num)
                {
                    try
                    {
                        regs.Add(PosixSignalRegistration.Create(s, ctx =>
                        {
                            ctx.Cancel = true;
                            try { if (!p.HasExited) Signals.Send(p.Id, num); } catch (InvalidOperationException) { }
                        }));
                    }
                    catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException) { }
                }
                Forward(PosixSignal.SIGTERM, Signals.SIGTERM);
                Forward(PosixSignal.SIGHUP, Signals.SIGHUP);
                Forward(PosixSignal.SIGQUIT, Signals.SIGQUIT);
                if (!tty) Forward(PosixSignal.SIGINT, Signals.SIGINT);
            }
            p.WaitForExit();
            return p.ExitCode;   // a child killed by a signal reports 128 + signal on Unix
        }
        finally
        {
            foreach (var r in regs) r.Dispose();
            Console.CancelKeyPress -= cancel;
        }
    }
}
