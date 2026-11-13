using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace IndexNode;

/// <summary>Finds yt-dlp, ffmpeg and ffprobe on PATH, or downloads pinned releases into the tools folder beside the config.</summary>
public sealed class Tools(HttpClient http)
{
    public const string YtDlpVersion = "2026.10.26";
    private string? ytdlp, ffmpeg, ffprobe;
    private readonly SemaphoreSlim gate = new(1, 1);

    private static string Exe(string n) => OperatingSystem.IsWindows() ? n + ".exe" : n;

    public static string? OnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var f = Path.Combine(dir, Exe(name)); if (File.Exists(f)) return f; } catch { }
        }
        return null;
    }

    private static string? InTools(string name) { var f = Path.Combine(NodeConfig.ToolsDir(), Exe(name)); return File.Exists(f) ? f : null; }

    public async Task<string> YtDlpAsync(CancellationToken ct)
    {
        if (ytdlp != null) return ytdlp;
        await gate.WaitAsync(ct);
        try
        {
            ytdlp ??= OnPath("yt-dlp") ?? InTools("yt-dlp") ?? await InstallYtDlpAsync(ct);
            return ytdlp;
        }
        finally { gate.Release(); }
    }

    public async Task<string> FfmpegAsync(CancellationToken ct) { await EnsureFfAsync(ct); return ffmpeg!; }
    public async Task<string> FfprobeAsync(CancellationToken ct) { await EnsureFfAsync(ct); return ffprobe!; }

    private async Task EnsureFfAsync(CancellationToken ct)
    {
        if (ffmpeg != null && ffprobe != null) return;
        await gate.WaitAsync(ct);
        try
        {
            ffmpeg = OnPath("ffmpeg") ?? InTools("ffmpeg");
            ffprobe = OnPath("ffprobe") ?? InTools("ffprobe");
            if (ffmpeg == null || ffprobe == null) { await InstallFfmpegAsync(ct); ffmpeg = InTools("ffmpeg"); ffprobe = InTools("ffprobe"); }
            if (ffmpeg == null || ffprobe == null) throw new InvalidOperationException("ffmpeg/ffprobe not found and could not be installed");
        }
        finally { gate.Release(); }
    }

    private async Task Download(string url, string dest, CancellationToken ct)
    {
        Log.Info($"tools: downloading {url}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        await using var fs = File.Create(dest);
        await r.Content.CopyToAsync(fs, ct);
    }

    private async Task<string> InstallYtDlpAsync(CancellationToken ct)
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        var asset = OperatingSystem.IsWindows() ? "yt-dlp.exe" : OperatingSystem.IsMacOS() ? "yt-dlp_macos" : arm ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";
        var dest = Path.Combine(NodeConfig.ToolsDir(), Exe("yt-dlp"));
        await Download($"https://github.com/yt-dlp/yt-dlp/releases/download/{YtDlpVersion}/{asset}", dest, ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dest, (UnixFileMode)0b111_101_101);
        return dest;
    }

    private async Task InstallFfmpegAsync(CancellationToken ct)
    {
        var tools = NodeConfig.ToolsDir();
        Directory.CreateDirectory(tools);
        var tmp = Path.Combine(tools, "dl");
        Directory.CreateDirectory(tmp);
        if (OperatingSystem.IsWindows())
        {
            var zip = Path.Combine(tmp, "ffmpeg.zip");
            await Download("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", zip, ct);
            using var za = ZipFile.OpenRead(zip);
            foreach (var e in za.Entries.Where(e => e.FullName.EndsWith("/bin/ffmpeg.exe") || e.FullName.EndsWith("/bin/ffprobe.exe")))
                e.ExtractToFile(Path.Combine(tools, e.Name), true);
        }
        else
        {
            var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            var archive = Path.Combine(tmp, "ffmpeg.tar.xz");
            // BtbN's Linux build, because the johnvansickle static builds have no libsvtav1
            await Download($"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-{(arm ? "linuxarm64" : "linux64")}-gpl.tar.xz", archive, ct);
            var r = await Proc.RunAsync("tar", ["-xJf", archive, "-C", tmp, "--wildcards", "*/bin/ffmpeg", "*/bin/ffprobe"], ct);
            if (r.ExitCode != 0) throw new InvalidOperationException("could not unpack ffmpeg: " + r.Stderr);
            foreach (var f in Directory.GetFiles(tmp, "ff*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) is "ffmpeg" or "ffprobe"))
            {
                var dest = Path.Combine(tools, Path.GetFileName(f));
                File.Move(f, dest, true);
                File.SetUnixFileMode(dest, (UnixFileMode)0b111_101_101);
            }
        }
        try { Directory.Delete(tmp, true); } catch { }
    }
}
