using System.CommandLine;
using System.CommandLine.IO;
using System.Text;
using System.Text.Json;
using CliWrap;
using DotNext.IO;
using YamlDotNet.Serialization;
using static System.Net.Mime.MediaTypeNames;

namespace Nexis.Azure.Utilities;

public record class DownloadPlaylist(IConsole Console, CancellationToken token) 
{
    public required string CookiesFilePath;

    public required string PlaylistIdUrlOrName;

    public string? TargetFilePath;

    public bool SkipIfExists = false;

    public string? PlaylistMapPath;

    public bool WriteRaw;

    public bool WriteProcessed;

    public int? Limit;

    public YoutubeFile[]? Files;

    public async Task<int> RunAsync()
    {
        Console.WriteLine($"Downloading {PlaylistIdUrlOrName} to {TargetFilePath}");
        if (SkipIfExists && File.Exists(TargetFilePath))
        {
            Console.WriteLine($"Found {PlaylistIdUrlOrName} to {TargetFilePath}");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(TargetFilePath)!);

        var stringBuilder = new StringBuilder();

        string playlistUrl;
        if (!PlaylistIdUrlOrName.StartsWith("https://"))
        {
            string playlistId = PlaylistIdUrlOrName;
            if (PlaylistMapPath != null)
            {
                var map = JsonSerializer.Deserialize<NameMap>(File.ReadAllText(PlaylistMapPath))!;
                if (map.ContainsKey(playlistId))
                {
                    playlistId = map[playlistId];
                }
            }

            playlistUrl = $"https://www.youtube.com/playlist?list={playlistId}";
        }
        else
        {
            playlistUrl = PlaylistIdUrlOrName;
        }

        try
        {
            await ExecAsync("yt-dlp", $"""
            --cookies "{CookiesFilePath}"
            --flat-playlist -J "{playlistUrl}"
            --playlist-items 1-{Limit ?? 10000}
            """.SplitArgs(),
                PipeTarget.ToStringBuilder(stringBuilder),
                Console.Error.CreateTextWriter(),
                isCliWrap: true);
        }
        catch (Exception ex)
        {
            throw;
        }

        var content = stringBuilder.ToString();

        Files = YoutubePlaylist.ReadFromVideoData(content);

        if (TargetFilePath != null)
        {
            if (WriteRaw)
            {
                File.WriteAllText(Path.ChangeExtension(TargetFilePath, "json"), content);
            }

            if (WriteProcessed)
            {
                File.WriteAllText(Path.ChangeExtension(TargetFilePath, "yaml"), new Serializer().Serialize(Files));
            }
        }

        return 0;
    }
}