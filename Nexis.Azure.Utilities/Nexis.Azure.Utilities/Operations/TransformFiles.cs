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
using static Nexis.Azure.Utilities.Helpers;

namespace Nexis.Azure.Utilities;

public class TransformFiles(IConsole Console, CancellationToken token)
{
    public required string LocalSourcePath;

    public required string OutputRoot;

    public required string CompletedTranslationFolder;

    public required string RelativePath;

    public string GdrivePath;

    public static bool SingleThreaded = System.Diagnostics.Debugger.IsAttached;

    public int ThreadCount = SingleThreaded ? 1 : 4;

    public int Limit = int.MaxValue;

    public int StageCapacity = 5;

    public required List<LanguageCode> Languages { get; set; } = [eng, jpn, kor, zho];

    public List<string> Extensions = [".mp4", ".avi", ".mkv", ".webm", ".m4v"];

    private ConcurrentDictionary<(Guid Id, LanguageCode Language), PendingTranslation> PendingTranslations = new();

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
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
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
        var result = inputs;
        foreach (var step in steps)
        {
            result = step(result);
        }

        var list = await result.ToListAsync();
    }

    public async Task<int> RunAsync()
    {
        if (!string.IsNullOrEmpty(RelativePath) && RelativePath.StartsWith(LocalSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            // If RelativePath is a full path, we need to remove the LocalSourcePath prefix
            RelativePath = RelativePath.Substring(LocalSourcePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var files = GetFiles();

        var entries = files.Select(e =>
        {
            var sourceVid = e.Value.FullName;
            var entry = new FileEntry(sourceVid, e.Key, sourceVid, sourceVid, Guid.NewGuid());
            return entry;
        }).ToAsyncEnumerable();

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
            )

        var splits = RunStageAsync("split", ThreadCount, entries, async (entry, token) =>
        {
            var splitter = new SplitAudio(Console, token)
            {
                VideoFile = entry.Origin,
                OutputFolder = entry.Target,
                OperationId = entry.OperationId
            };

            await splitter.RunAsync();
        });

        var translations = RunStageAsync("translate", 1, splits, async (entry, token) =>
        {
            var videoWrappedAudioFiles = Directory.GetFiles(entry.Source, "*.mp4");
            entry.SegmentCount = videoWrappedAudioFiles.Length;
            foreach (var audioFile in videoWrappedAudioFiles)
            {
                var op = new TranslateOperation(Console, token)
                {
                    AudioFile = audioFile,
                    GdrivePath = GdrivePath,
                    Languages = Languages,
                };

                await op.RunAsync();
            }
        });

        var translationsByLanguage = translations.SelectMany(f => Languages.Select(l => f with { Language = l }).ToAsyncEnumerable());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var translationCompletions = WaitForTranslationsAsync(cts);

        await RunPipeline(
            i => )

        var downloads = RunStageAsync("download", 1, translationsByLanguage, async (entry, token) =>
        {
            // Wait for translations to complete
            Contract.Assert(entry.SegmentCount.HasValue);
            var pending = AddPending(entry.OperationId, entry.Language.Value);
            pending.FileEntry = entry;
            pending.CompleteIfReady();

            await Task.WhenAny(pending.Completion.Task, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token)).Unwrap();

            var videoIdFiles = Directory.GetFiles(pending.OutputFolder, "*.json");
            foreach (var videoIdFile in videoIdFiles)
            {
                var record = TranslationRecord.ReadFromFile(videoIdFile);
                var language = record.GetLanguageCode();
                var op = new DownloadTranslation(Console, token)
                {
                    VideoId = record.event_data.video_translate_id,
                    TargetFolder = entry.Target,
                    BaseName = record.FileName,
                    Language = language,
                    Delete = true
                };

                if (Exists(op.SubFile) && Exists(op.VideoFile))
                {
                    continue;
                }

                await op.RunAsync();
            }
        },
        endSignal: cts);

        var outputs = RunStageAsync("output", 1, downloads, async (entry, token) =>
        {
            var op = new MergeAudio(Console, token)
            {
                InputFolder = entry.Source,
                OutputAudioFile = entry.Target.Replace(".mp4", "") + ".ogg",
            };

            await op.RunAsync();
        });


        var list = await outputs.ToArrayAsync();

        await translationCompletions;

        return 0;
    }

    private async Task WaitForTranslationsAsync(CancellationTokenSource cts)
    {
        while (!cts.Token.IsCancellationRequested)
        {
            var files = Directory.Exists(CompletedTranslationFolder)
                ? Directory.GetFiles(CompletedTranslationFolder, "*.json")
                : [];
            foreach (var file in files)
            {
                try
                {
                    var text = File.ReadAllText(file);

                    var record = TranslationRecord.Parse(text);
                    var info = ExtractFileDescriptor(record.FileName);
                    var language = record.GetLanguageCode();
                    var pending = AddPending(info.Id, language);

                    File.WriteAllText(Path.Combine(pending.OutputFolder, record.FileName + ".json"), text);
                    File.Delete(file);
                    pending.CompletedSegmentCount++;
                    pending.CompleteIfReady();
                }
                catch
                {

                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cts.Token);
            }
            catch
            {
            }
        }
    }

    private PendingTranslation AddPending(Guid id, LanguageCode language)
    {
        var pending = PendingTranslations.GetOrAdd((id, language), static k => new PendingTranslation(k.Id, k.Language));

        if (pending.OutputFolder == null)
        {
            pending.OutputFolder ??= Path.Combine(OutputRoot, "completions", pending.FolderName);

            Directory.CreateDirectory(pending.OutputFolder);

            pending.CompletedSegmentCount = Directory.GetFiles(pending.OutputFolder, "*.json").Length;
        }

        return pending;
    }

    public async IAsyncEnumerable<FileEntry> RunStageAsync(
        Stages stage,
        Parallelism parallelism,
        IAsyncEnumerable<FileEntry> items,
        Func<FileEntry, CancellationToken, ValueTask> runAsync,
        bool readMarker = true,
        CancellationTokenSource? endSignal = default,
        bool splitByLanguage = false)
    {
        var stageName = stage.ToString();
        bool isRunning = RunStages.Contains(stage);
        var root = Path.Combine(OutputRoot, stageName);
        var channel = Channel.CreateBounded<FileEntry>(StageCapacity);


        async Task iterateAsync()
        {
            try
            {
                await Task.Yield();

                await ForEachAsync(parallelism, items, token, async (entry, token) =>
                {
                    var suffix = entry.Language is { } lang ? $".{lang}" : "";
                    entry = entry with
                    {
                        Source = entry.Target,
                        Target = Path.GetFullPath(Path.Combine(root, entry.RelativePath + suffix))
                    };
                    var marker = entry.Target + ".marker";
                    if (!Exists(marker))
                    {
                        if (!isRunning)
                        {
                            return;
                        }

                        await runAsync.Invoke(entry, token);
                        File.WriteAllText(marker, JsonSerializer.Serialize(
                            new MarkerData(entry.OperationId, entry.SegmentCount)));
                    }
                    else if (readMarker)
                    {
                        var markerData = JsonSerializer.Deserialize<MarkerData>(Out.Var(out var mt, File.ReadAllText(marker)));
                        entry = entry with { OperationId = markerData.Id, SegmentCount = markerData.SegmentCount };
                    }

                    await channel.Writer.WriteAsync(entry);
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

    public record PendingTranslation(Guid OperationId, LanguageCode Language)
    {
        public string OutputFolder = null!;
        public string FolderName = $"{OperationId:n}.{Language}";
        public int CompletedSegmentCount;
        public FileEntry? FileEntry;

        public bool IsQueued = false;

        public void CompleteIfReady()
        {
            if (IsQueued) return;

            lock (this)
            {
                if (!IsQueued && CompletedSegmentCount == FileEntry?.SegmentCount)
                {
                    IsQueued = true;
                    Completion.SetResult();
                }
            }
        }

        public TaskCompletionSource Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public record FileEntry(string Origin, string RelativePath, string Source, string Target, Guid OperationId, LanguageCode? Language = default)
    {
        public int? SegmentCount;
    }

    private record MarkerData(Guid Id, int? SegmentCount);

    private bool Exists(string splitMarker) => File.Exists(splitMarker);
}