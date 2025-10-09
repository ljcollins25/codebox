using System;
using System.CommandLine;
using System.CommandLine.IO;
using System.Text;
using System.Text.Json;
using CliWrap;
using DotNext.IO;
using YamlDotNet.Serialization;
using static Nexis.Azure.Utilities.DeleteRequest;

namespace Nexis.Azure.Utilities;

public record class ProcessPlaylist(IConsole Console, CancellationToken token) 
{
    public required string SourceFilePath;

    public required string TargetFilePath;

    public int? Limit;

    //public required string GDrivePath;

    public string TranslateUrl = Constants.TranslateUrl;
    public string SummarizeUrl = Constants.SummarizeUrl;
    public IDictionary<string, YoutubeFile> SourcePlaylist = null!;

    public bool FailFast;

    public IAsyncEnumerable<YoutubeFile> ProcessAsync()
    {
        Console.WriteLine($"Downloading {SourceFilePath} to {TargetFilePath}");

        SourcePlaylist = YoutubePlaylist.Deserialize(File.ReadAllText(SourceFilePath), Limit);
        var targetPlaylist = YoutubePlaylist.Deserialize(File.Exists(TargetFilePath) ? File.ReadAllText(TargetFilePath) : "[]").ToConcurrent();
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        invalidChars.Add('$');
        Directory.CreateDirectory(Path.GetDirectoryName(TargetFilePath)!);

        int count = 0;

        return SourcePlaylist.Values.ToAsyncEnumerable().SplitMergeAsync(
            predicate: y => targetPlaylist.ContainsKey(y.Id),
            handleFalseItems: newFiles =>
            {
                return newFiles.ChunkAsync(8).ParallelSelectAsync(true, token, async (files, token) =>
                {
                    using var client = new HttpClient();
                    //var drivePath = GDrivePath + $"{Path.GetFileName(SourceFilePath)}.{Timestamp.Now}.{files[0].Id}.yaml";
                    //Console.WriteLine($"Processing {files.Length} entries to {drivePath}");
                    //client.DefaultRequestHeaders.Add("TargetPath", Path.GetFileName(drivePath));

                    var result = new ProcessResult();
                    var sourceLines = string.Join("\n", files.Select((f, index) => $"- $ {f.Title}"));
                    result.translated = await client.PostTextAsync(TranslateUrl, sourceLines);

                    if (result.TranslatedLines.Length == files.Length)
                    {
                        result.summarized = await client.PostTextAsync(SummarizeUrl, string.Join("\n", result.TranslatedLines.Select((f, index) => $"- $ {f}")));
                    }

                    if (result != null && result.TranslatedLines.Length == files.Length && result.SummarizedLines.Length == files.Length)
                    {
                        for (int i = 0; i < files.Length; i++)
                        {
                            var file = files[i];
                            file.TranslatedTitle = result.TranslatedLines[i].Where(c => !invalidChars.Contains(c)).ToArray().AsSpan().ToString();
                            file.ShortTitle = result.SummarizedLines[i];
                            var shortTitle = file.ShortTitle.TrimEnd('.').Trim().Where(c => !invalidChars.Contains(c)).ToArray().AsSpan().ToString();
                            file.ShortTitle = shortTitle?.Replace(": ", " - ")!;

                            targetPlaylist[file.Id] = file;
                        }

                        lock (targetPlaylist)
                        {
                            File.WriteAllText(TargetFilePath, YoutubePlaylist.Serialize(targetPlaylist.Values));
                            count += files.Length;
                            Console.WriteLine($"Processed {count} video entries");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"""
                            Failed to process video entries (Count: {files.Length}, Translated: {result?.TranslatedLines.Length}, Summarized: {result?.SummarizedLines.Length})
                            [{string.Join(", ", files.Select(f => f.Id))}]
                            ``` sourceLines
                            {sourceLines}
                            ``` translated
                            {result?.translated}
                            ``` summarized
                            {result?.summarized}
                            ```
                            """);
                    }

                    return files;
                }).SelectMany(files => files.ToAsyncEnumerable());
            })
            .Where(f => targetPlaylist.ContainsKey(f.Id))
            .Select(f => targetPlaylist[f.Id])
            .Where(f => !string.IsNullOrWhiteSpace(f.ShortTitle));
    }

    public async Task<int> RunAsync()
    {
        //Url = "https://hook.us2.make.com/d8b12eyj19j24gtrzqkjgh3hgikubpa2";

        await ProcessAsync().ForEachAsync(file => { }, token);
        
        return 0;
    }

    private static readonly char[] TrimChars = "- $".ToArray();

    private record ProcessResult
    {
        public string translated { get; set; } = "";
        public string summarized { get; set; } = "";

        private string[]? translatedLines;
        private string[]? summarizedLines;
        public string[] TranslatedLines { get => translatedLines ?? translated.SplitLines().Select(t => t.Trim(TrimChars)).Where(s => s.IsNonEmpty()).ToArray(); set => translatedLines = value; }
        public string[] SummarizedLines { get => summarizedLines ?? summarized.SplitLines().Select(t => t.Trim(TrimChars)).Where(s => s.IsNonEmpty()).ToArray(); set => summarizedLines = value; }
    }
}