using System.CommandLine;
using System.CommandLine.IO;
using System.Text;
using CliWrap;
using DotNext.IO;
using Microsoft.Playwright;

namespace Nexis.Azure.Utilities;

public record class DownloadYoutubeVideo(IConsole Console, CancellationToken token)
{
    public required string Id;

    public string Title = "???";

    public required string TargetFileBase;

    public required string CookiesFilePath;

    public FileKinds Kinds = FileKinds.video;

    public enum FileKinds
    {
        video,
        captions,
        thumbnail
    }

    public async Task<int> RunAsync()
    {
        Console.WriteLine($"Downloading Youtube video '{Id}' to {TargetFileBase}:\nTitle={Title}");

        Directory.CreateDirectory(Path.GetDirectoryName(TargetFileBase)!);

        var url = $"https://www.youtube.com/watch?v={Id}";

        var sb = new StringBuilder();
        PipeTargetValue target = Console.Out.CreateTextWriter();
        target &= sb;

        target = sb;

        try
        {
            await ExecAsync(@"yt-dlp", $"""
            --no-progress
            -f "(bestvideo[vcodec*=hevc][height<=1080]/bestvideo[height<=1080]) +bestaudio"
            -o "{TargetFileBase}.%(ext)s" --merge-output-format mp4
            --cookies "{CookiesFilePath}"
            --write-thumbnail --convert-thumbnails jpg
            --write-subs --sub-lang en --convert-subs srt "{url}"
            """.SplitArgs(),
                target);
        }
        catch (Exception ex)
        {
            try
            {
                await ExecAsync(@"yt-dlp", $"""
            -o "{TargetFileBase}.%(ext)s" --merge-output-format mp4
            --cookies "{CookiesFilePath}"
            --write-thumbnail --convert-thumbnails jpg
            --write-subs --sub-lang en --convert-subs srt {url}
            """.SplitArgs(),
                    target);
            }
            catch (Exception ex2)
            {
                throw ex2;
            }
        }

        return 0;
    }
}