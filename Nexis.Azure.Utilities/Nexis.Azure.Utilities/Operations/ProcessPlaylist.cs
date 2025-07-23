using System;
using System.CommandLine;
using System.CommandLine.IO;
using System.Text;
using System.Text.Json;
using CliWrap;
using DotNext.Collections.Generic;
using DotNext.IO;
using YamlDotNet.Serialization;

namespace Nexis.Azure.Utilities;

public record class ProcessPlaylist(IConsole Console, CancellationToken token) 
{
    public required string SourceFilePath;

    public required string TargetFilePath;

    public int? Limit;

    public required string GDrivePath;

    public string Url = "https://hooks.zapier.com/hooks/catch/15677350/uoxtvz4/";
    public IDictionary<string, YoutubeFile> SourcePlaylist = null!;

    public bool FailFast;

    public async Task<int> RunAsync()
    {
        //Url = "https://hook.us2.make.com/d8b12eyj19j24gtrzqkjgh3hgikubpa2";


        Console.WriteLine($"Downloading {SourceFilePath} to {GDrivePath}");

        SourcePlaylist = YoutubePlaylist.Deserialize(File.ReadAllText(SourceFilePath), Limit);
        var targetPlaylist = YoutubePlaylist.Deserialize(File.Exists(TargetFilePath) ? File.ReadAllText(TargetFilePath) : "[]");
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        invalidChars.Add('$');
        Directory.CreateDirectory(Path.GetDirectoryName(TargetFilePath)!);

        int count = 0;

        var newFiles = SourcePlaylist.Values.ExceptBy(targetPlaylist.Keys, y => y.Id).ToArray();

        foreach (var files in newFiles.Chunk(8))
        {
            using var client = new HttpClient();
            var drivePath = GDrivePath + $"{Path.GetFileName(SourceFilePath)}.{Timestamp.Now}.yaml";
            Console.WriteLine($"Processing {files.Length} entries to {drivePath}");
            client.DefaultRequestHeaders.Add("TargetPath", Path.GetFileName(drivePath));
            var response = await client.PostAsync(Url, new StringContent(string.Join("\n", files.Select((f, index) => $"- ${f.Title}"))));

            response.EnsureSuccessStatusCode();

            var sb = new StringBuilder();
            var iterations = 10;
            ProcessResult result = null!;

            for (int i = 1; i <= iterations; i++)
            {
                await Task.Delay(1000);

                try
                {
                    sb.Clear();
                    await ExecAsync("rclone", ["cat", drivePath], sb);
                    sb.Replace("\n- ", "\n- $");
                    sb.Replace("-$", "- $");
                    sb.Replace(": ", "꞉ ");//.Replace("#", "＃");
                    result = YamlDeserialize<ProcessResult>(sb.ToString())!;
                    await ExecAsync("rclone", ["delete", drivePath]);
                    break;
                }
                catch (Exception ex) when (i != iterations || !FailFast)
                {
                    
                }
            }

            if (result == null)
            {
                continue;
            }

            sb.Replace("- ", "-  ");

            for (int i = 0; i < files.Length; i++)
            {
                var file = files[i];
                file.TranslatedTitle = result.TranslatedLines[i].Where(c => !invalidChars.Contains(c)).ToArray().AsSpan().ToString();
                file.ShortTitle = result.SummarizedLines[i];
                var shortTitle = file.ShortTitle.TrimEnd('.').Trim().Where(c => !invalidChars.Contains(c)).ToArray().AsSpan().ToString();

                file.ShortTitle = shortTitle;
                targetPlaylist[file.Id] = file;
            }

            File.WriteAllText(TargetFilePath, YoutubePlaylist.Serialize(targetPlaylist.Values));
            count += files.Length;
            Console.WriteLine($"Processed {count} video entries");
        }

        foreach (var item in targetPlaylist)
        {
            item.Value.ShortTitle = item.Value.ShortTitle?.Replace(": ", " - ")!;
            if (SourcePlaylist.ContainsKey(item.Key))
            {
                SourcePlaylist[item.Key] = item.Value;
            }
        }

        SourcePlaylist.Where(s => string.IsNullOrWhiteSpace(s.Value.ShortTitle)).ToArray().ForEach(e => SourcePlaylist.Remove(e.Key));

        File.WriteAllText(TargetFilePath, YoutubePlaylist.Serialize(targetPlaylist.Values));
        return 0;
    }

    private static readonly char[] TrimChars = "- ".ToArray();

    private record ProcessResult
    {
        public string translated { get; set; } = "";
        public string summarized { get; set; } = "";

        private string[]? translatedLines;
        private string[]? summarizedLines;
        public string[] TranslatedLines { get => translatedLines ?? translated.SplitLines().Select(t => t.Trim(TrimChars)).ToArray(); set => translatedLines = value; }
        public string[] SummarizedLines { get => summarizedLines ?? summarized.SplitLines().Select(t => t.Trim(TrimChars)).ToArray(); set => summarizedLines = value; }
    }
}