using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Xml.Linq;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using DotNext;
using DotNext.IO;
using Microsoft.Playwright;
using Nikse.SubtitleEdit.Core.Common;
using static Nexis.Azure.Utilities.Helpers;

namespace Nexis.Azure.Utilities;

public class TransformFiles(IConsole Console, CancellationToken token)
{
    public required string LocalSourcePath;

    public required string OutputRoot;

    public required string CompletedTranslationFolder;

    public required string RelativePath;

    public string? GdrivePath;

    public Uri? UploadUri;

    public static bool SingleThreaded = System.Diagnostics.Debugger.IsAttached;

    public int ThreadCount = SingleThreaded ? 1 : 4;

    public int Limit = int.MaxValue;

    public int Skip = 0;

    public int StageCapacity = 5;

    public required IReadOnlyList<LanguageCode> Languages { get; set; } = [eng, jpn, kor, zho];

    public List<string> Extensions = [".mp4", ".avi", ".mkv", ".webm", ".m4v"];

    private ConcurrentDictionary<(Vuid Id, LanguageCode Language), PendingTranslation> PendingTranslations = new();

    public bool RestoreState = false;

    public bool ApiMode = true;

    public string ShutdownFilePath => Path.Combine(OutputRoot, "gracefulshutdown.txt");

    public IDictionary<string, FileInfo> GetFiles()
    {
        LocalSourcePath = Path.GetFullPath(Path.Combine(LocalSourcePath, RelativePath ?? string.Empty));
        var rootPath = File.Exists(LocalSourcePath) ? Path.GetDirectoryName(LocalSourcePath) : LocalSourcePath;
        rootPath = rootPath!.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;

        var relativePath = RelativePath;
        if (!string.IsNullOrEmpty(RelativePath) && File.Exists(LocalSourcePath))
        {
            relativePath = Path.GetDirectoryName(relativePath) ?? string.Empty;
        }

        string getPath(string path)
        {
            if (!string.IsNullOrEmpty(relativePath))
            {
                path = Path.Combine(relativePath, path).Replace('\\', '/');
            }

            return path;
        }

        var files = new DirectoryInfo(rootPath).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.FullName.StartsWith(LocalSourcePath, StringComparison.OrdinalIgnoreCase))
            .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.DirectoryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, VideoFileNameComparer)
            .Skip(Skip)
            .Take(Limit)
            .ToImmutableSortedDictionary(f => getPath(f.FullName.Substring(rootPath.Length).Replace('\\', '/')), f => f, StringComparer.OrdinalIgnoreCase)
            .ToBuilder()
            ;

        return files;
    }

    public List<Stages> RunStages = Enum.GetValues<Stages>().ToList();

    public enum Stages
    {
        split,
        translate,
        download,
        output,
        upload
    }

    public async Task RunPipeline(
        IAsyncEnumerable<FileEntry> inputs,
        params Func<IAsyncEnumerable<FileEntry>, IAsyncEnumerable<FileEntry>>[] steps)
    {
        try
        {
            if (File.Exists(ShutdownFilePath))
            {
                // File wasn't cleaned up so there was an abrupt shutdown
                // need to restore state
                RestoreState = true;
            }

            File.WriteAllText(ShutdownFilePath, DateTime.Now.ToString("o"));

            var result = inputs;
            foreach (var step in steps)
            {
                result = step(result);
            }

            var list = await result.ToListAsync();
        }
        finally
        {
            // Delete file to indicate graceful shutodwn
            File.Delete(ShutdownFilePath);
        }
    }

    public async Task<int> RunAsync()
    {
        if (!string.IsNullOrEmpty(RelativePath) && RelativePath.StartsWith(LocalSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            // If RelativePath is a full path, we need to remove the LocalSourcePath prefix
            RelativePath = RelativePath.Substring(LocalSourcePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var files = GetFiles();

        if (UploadUri == null)
        {
            RunStages.Remove(Stages.upload);
        }

        var entries = files.Select(e =>
        {
            var sourceVid = e.Value.FullName;
            var entry = new FileEntry(sourceVid, e.Key, sourceVid, sourceVid, Vuid.FromFileName(sourceVid));
            return entry;
        }).ToAsyncEnumerable();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var translationCompletions = WaitForTranslationsAsync(cts);

        await RunPipeline(
            entries,
            i => RunStageAsync(Stages.split, ThreadCount, i, async (entry, token) =>
            {
                var splitter = new SplitAudio(Console, token)
                {
                    VideoFile = entry.Origin,
                    OutputFolder = entry.Target,
                    OperationId = entry.OperationId
                };

                await splitter.RunAsync();
            }),
            i => RunStageAsync(Stages.translate, 1, i, async (entry, token) =>
            {
                var videoWrappedAudioFiles = entry.GetSplitFiles();
                entry.SegmentCount = videoWrappedAudioFiles.Length;
                int index = 0;

                var languages = Languages;

                foreach (var audioFile in videoWrappedAudioFiles)
                {
                    var op = new TranslateOperation(Console, token)
                    {
                        AudioFile = audioFile,
                        GdrivePath = GdrivePath?.UriCombine($"{entry.OperationId:n}-{index++}{Path.GetExtension(audioFile)}"),
                        Languages = Languages,
                    };

                    await op.RunAsync();
                }
            }),
            i => i.SelectMany(f => Languages.Select(l => f with { Language = l }).ToAsyncEnumerable()),

            i => RunStageAsync(Stages.download, ThreadCount * Languages.Count, i, async (entry, token) =>
            {
                // Wait for translations to complete
                Contract.Assert(entry.SegmentCount.HasValue);
                var pending = AddPending(entry.OperationId, entry.Language!.Value);
                pending.FileEntry = entry;
                pending.CompleteIfReady();

                await Task.WhenAny(pending.Completion.Task, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token)).Unwrap();

                //var videoIdFiles = Directory.GetFiles(pending.OutputFolder, "*.json");
                //foreach (var videoIdFile in videoIdFiles)
                foreach (var record in pending.RecordsByIndex.Values)
                {
                    //var record = TranslationRecord.ReadFromFile(videoIdFile);
                    var language = record.GetLanguageCode();
                    var op = new DownloadTranslation(Console, token)
                    {
                        VideoId = record.event_data.video_translate_id,
                        TargetFolder = entry.Target,
                        BaseName = record.FileName,
                        Language = language,
                        Delete = false,
                        CompletedFolderId = "c47f8b0ae3db4e58b948994021ff3100"
                    };

                    if (Exists(op.SubFile) && Exists(op.VideoFile))
                    {
                        continue;
                    }

                    await op.RunAsync();
                }
            },
            endSignal: cts),
            i => RunStageAsync(Stages.output, 1, i, async (entry, token) =>
            {
                var op = new MergeAudio(Console, token)
                {
                    InputFolder = entry.Source,
                    OutputAudioFile = entry.Target,
                };

                await op.RunAsync();
            },
            preRun: (entry, token) =>
            {
                entry.Target = entry.Target.Replace(".mp4", "") + ".ogg";
            }),
            i => RunStageAsync(Stages.upload, ThreadCount, i, async (entry, token) =>
            {
                var op = new UploadFilesOperation(Console, token)
                {
                    Force = true,
                    RequiredInfixes = [Path.GetFileNameWithoutExtension(entry.Source)],
                    LocalSourcePath = GetStageRoot(Stages.output),
                    ThreadCount = 1,
                    RelativePath = Path.GetDirectoryName(entry.Source)!,
                    Uri = UploadUri!,
                    ExcludedExtensions = [".marker"],
                    UpdateTimestamps = true
                };

                await op.RunAsync();
            }));


        await translationCompletions;

        return 0;
    }

    private async Task WaitForTranslationsAsync(CancellationTokenSource cts)
    {
        HashSet<string> failures = new();
        HashSet<string> exists = new();

        while (!cts.Token.IsCancellationRequested)
        {
            if (ApiMode)
            {
                var list = new ListVideosOperation(Console, cts.Token)
                {
                    Print = false
                };

                await list.RunAsync();

                foreach (var item in list.Results.Where(r => r.status == VideoStatus.completed))
                {
                    var info = item.GetInfo();
                    if (info.Index < 0) continue;

                    var pending = AddPending(info.Id, item.output_language);
                    if (pending.FileEntry == null) continue;
                    
                    var record = TranslationRecord.FromVideoItem(item);
                    var targetPath = Path.Combine(pending.OutputFolder, record.FileName + ".json");
                    if (exists.Add(targetPath))
                    {
                        pending.RecordsByIndex[info.Index] = record;

                        //File.WriteAllText(targetPath, JsonSerializer.Serialize(record));
                        //pending.CompletedSegmentCount++;
                        pending.CompleteIfReady();
                    }
                }
            }
            else
            {
                var files = Directory.Exists(CompletedTranslationFolder)
                    ? Directory.GetFiles(CompletedTranslationFolder, "*.json")
                    : [];
                foreach (var file in files)
                {
                    try
                    {
                        if (failures.Contains(file)) continue;

                        var text = File.ReadAllText(file);

                        var record = TranslationRecord.Parse(text);
                        if (record.event_type.Contains("fail"))
                        {
                            failures.Add(file);
                            continue;
                        }

                        var info = ExtractFileDescriptor(record.FileName);
                        var language = record.GetLanguageCode();
                        var pending = AddPending(info.Id, language);

                        File.WriteAllText(Path.Combine(pending.OutputFolder, record.FileName + ".json"), text);
                        File.Delete(file);
                        //pending.CompletedSegmentCount++;
                        throw new NotImplementedException();
                        pending.CompleteIfReady();
                    }
                    catch
                    {

                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(300), cts.Token);
            }
            catch
            {
            }
        }
    }

    private PendingTranslation AddPending(Vuid id, LanguageCode language)
    {
        var pending = PendingTranslations.GetOrAdd((id, language), static k => new PendingTranslation(k.Id, k.Language));

        if (pending.OutputFolder == null)
        {
            pending.OutputFolder ??= Path.Combine(OutputRoot, "completions", pending.FolderName);

            Directory.CreateDirectory(pending.OutputFolder);

            //pending.CompletedSegmentCount = Directory.GetFiles(pending.OutputFolder, "*.json").Length;
        }

        return pending;
    }

    private string GetStageRoot(Stages stage) => Path.Combine(OutputRoot, stage.ToString());

    public async IAsyncEnumerable<FileEntry> RunStageAsync(
        Stages stage,
        Parallelism parallelism,
        IAsyncEnumerable<FileEntry> items,
        Func<FileEntry, CancellationToken, ValueTask> runAsync,
        bool readMarker = true,
        CancellationTokenSource? endSignal = default,
        bool splitByLanguage = false,
        Action<FileEntry, CancellationToken>? preRun = null)
    {
        var stageName = stage.ToString();
        bool isRunning = RunStages.Contains(stage);
        var root = GetStageRoot(stage);
        var channel = Channel.CreateBounded<FileEntry>(StageCapacity);


        async Task iterateAsync()
        {
            try
            {
                await Task.Yield();

                await ForEachAsync(parallelism, items, token, async (entry, token) =>
                {
                    string result = "Succeeded";
                    try
                    {
                        Console.WriteLine($"[{stageName}] Start {entry.RelativePath} [{entry.Language}]");
                        var suffix = entry.Language is { } lang ? $".{lang}" : "";
                        entry = entry with
                        {
                            Source = entry.Target,
                            Target = Path.GetFullPath(Path.Combine(root, entry.RelativePath + suffix))
                        };
                        var marker = entry.Target + ".marker";
                        CreateDirectoryForFile(marker);
                        if (stage == Stages.translate && RestoreState)
                        {
                            var id = entry.OperationId.ToString().Substring(0, 8);
                            entry.SegmentCount = Directory.GetFiles(entry.Source, "*.mp4")
                                .Where(f => f.Contains(id))
                                .Count();
                        }

                        preRun?.Invoke(entry, token);

                        if (!Exists(marker) || new FileInfo(marker).Length == 0)
                        {
                            if (!isRunning)
                            {
                                result = "Skipping";
                                return;
                            }

                            Console.WriteLine($"[{stageName}] Running {entry.RelativePath} [{entry.Language}]");
                            await runAsync.Invoke(entry, token);
                            File.WriteAllText(marker, JsonSerializer.Serialize(
                                new MarkerData(entry.OperationId, entry.SegmentCount)));
                        }
                        else if (readMarker)
                        {
                            var markerData = JsonSerializer.Deserialize<MarkerData>(Out.Var(out var mt, File.ReadAllText(marker)));
                            entry = entry with { OperationId = markerData!.Id, SegmentCount = entry.SegmentCount ?? markerData.SegmentCount };
                            result = "Up to date";

                            if (RestoreState)
                            {
                                markerData = markerData with { SegmentCount = entry.SegmentCount };
                                File.WriteAllText(marker, JsonSerializer.Serialize(markerData));
                            }
                        }

                        await channel.Writer.WriteAsync(entry);
                    }
                    catch (Exception ex)
                    {
                        result = $"Failed: \n{ex}";
                    }
                    finally
                    {
                        Console.WriteLine($"[{stageName}] End {entry.RelativePath} [{entry.Language}]. Result = {result}");
                    }
                });
            }
            finally
            {
                endSignal?.Cancel();
                channel.Writer.Complete();
            }
        }


        var task = iterateAsync();

        await foreach (var item in channel.Reader.ReadAllAsync())
        {
            yield return item;
        }

        await task;
    }

    public record PendingTranslation(Vuid OperationId, LanguageCode Language)
    {
        public string OutputFolder = null!;
        public string FolderName = $"{OperationId:n}.{Language}";
        //public int CompletedSegmentCount;
        public FileEntry? FileEntry;

        public ConcurrentDictionary<int, TranslationRecord> RecordsByIndex = new();

        public bool IsQueued = false;

        public void CompleteIfReady()
        {
            if (IsQueued) return;

            lock (this)
            {
                if (!IsQueued && RecordsByIndex.Count == FileEntry?.SegmentCount)
                {
                    IsQueued = true;
                    Completion.SetResult();
                }
            }
        }

        public TaskCompletionSource Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public record FileEntry(string Origin, string RelativePath, string Source, string Target, Vuid OperationId, LanguageCode? Language = default)
    {
        public int? SegmentCount;

        public string[]? OverrideLanguages;

        public bool ForceRun { get; set; }

        public string Target { get; set; } = Target;
 
        public string[] GetSplitFiles() => Directory.GetFiles(Source, "*.mp4")
                                .Where(f => f.Contains(OperationId.ToString().Substring(0, 8))).ToArray();
    }

    public record MarkerData(Vuid Id, int? SegmentCount);

    private bool Exists(string splitMarker) => File.Exists(splitMarker);
}