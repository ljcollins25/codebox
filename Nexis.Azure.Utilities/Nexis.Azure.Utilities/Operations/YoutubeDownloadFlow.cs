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

public class YoutubeDownloadFlow(IConsole Console, CancellationToken token)
{
    public required List<string> Sources;

    public required string CookiesFilePath;

    public string? PlaylistMapPath;

    public required string OutputRoot;

    public required string GdrivePath;

    public Uri? UploadUri;

    public static bool SingleThreaded = System.Diagnostics.Debugger.IsAttached;

    public int ThreadCount = SingleThreaded ? 1 : 4;

    public int? Limit;

    public int Skip = 0;

    public int StageCapacity = 5;

    public bool RefreshPlaylists = false;

    public bool RestoreState = false;

    public List<string> Tags = [];

    public string ShutdownFilePath => Path.Combine(OutputRoot, "gracefulshutdown.txt");

    public List<Stages> RunStages = Enum.GetValues<Stages>().ToList();
    public List<Stages> ExcludeStages = [];

    public enum Stages
    {
        cookies,
        getmetadata,
        process,
        download,
        upload,
        cleanup
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
        RunStages.RemoveAll(s => ExcludeStages.Contains(s));
        var entries = Sources.Select(source =>
        {
            var uri = new Uri(source, UriKind.RelativeOrAbsolute);
            var name = uri.IsAbsoluteUri ? (uri.Query.AsNonEmptyOrNull() ?? source) : source;
            name = name.TrimStart('?');
            var entry = new FileEntry(Origin: source, RelativePath: name, Source: name, Target: name);
            return entry;
        }).ToAsyncEnumerable();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        if (UploadUri == null)
        {
            RunStages.Remove(Stages.upload);
        }

        bool isRun = false;
        await RunPipeline(
            entries,
            i => RunStageAsync(Stages.cookies, 1, i, async (entry, token) =>
            {
                if (isRun) return;
                isRun = true;
                var op = new GetYoutubeCookies(Console, token)
                {
                    TargetFile = CookiesFilePath
                };

                await op.RunAsync();
            },
            force: true),
            i => RunStageAsync(Stages.getmetadata, ThreadCount, i, async (entry, token) =>
            {
                var dl = new DownloadPlaylist(Console, token)
                {
                    Limit = Limit,
                    SkipIfExists = !RefreshPlaylists,
                    CookiesFilePath = CookiesFilePath,
                    PlaylistIdUrlOrName = entry.Origin,
                    PlaylistMapPath = PlaylistMapPath,
                    WriteProcessed = true,
                    TargetFilePath = entry.Target += ".metadata.yaml"
                };

                await dl.RunAsync();
            },
            force: true
            ),
            i => RunStageAsync(Stages.process, 1, i, async (entry, token) =>
            {
                var dl = new ProcessPlaylist(Console, token)
                {
                    SourceFilePath = entry.Source,
                    Limit = Limit,
                    GDrivePath = GdrivePath,
                    TargetFilePath = Path.Combine(GetStageRoot(Stages.process), "videos.yml"),
                };

                await dl.RunAsync();

                entry.Results = dl.SourcePlaylist.Values.Select(f =>
                {
                    return entry with
                    {
                        File = f,
                        RelativePath = $"{f.ShortTitle} {{yt-{f.Id}}}"
                    };
                }).ToArray();
            },
            force: true
            ),
            i => RunStageAsync(Stages.download, ThreadCount, i.Skip(Skip).Take(Limit ?? int.MaxValue), async (entry, token) =>
            {
                var op = new DownloadYoutubeVideo(Console, token)
                {
                    Id = entry.File.Id,
                    Title = entry.File.TranslatedTitle ?? entry.File.ShortTitle ?? entry.File.Title,
                    CookiesFilePath = CookiesFilePath,
                    TargetFileBase = Path.Combine(entry.Target, entry.File.ShortTitle!)
                };

                await op.RunAsync();
            }),
            i => RunStageAsync(Stages.upload, ThreadCount, i, async (entry, token) =>
            {
                //foreach (var tag in Tas)
                //{
                //    File.WriteAllBytes(Path.Combine(entry.Source, tag + ".tag"), Array.Empty<byte>());
                //}

                var op = new UploadFilesOperation(Console, token)
                {
                    Force = true,
                    LocalSourcePath = entry.Source,
                    ThreadCount = 1,
                    IncludeDirectory = true,
                    Uri = UploadUri!,
                    ExcludedExtensions = [".marker"],
                    UpdateTimestamps = true
                };

                await op.RunAsync();
            },
            preRun: (entry, token) =>
            {
                entry.Target = entry.Source;
            }),
            i => RunStageAsync(Stages.cleanup, ThreadCount, i, async (entry, token) =>
            {
                if (Directory.Exists(entry.Source))
                {
                    Directory.Delete(entry.Source, recursive: true);
                }
            })
            );

        return 0;
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
        Action<FileEntry, CancellationToken>? preRun = null,
        bool force = false)
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

                await ForEachAsync(parallelism, items.DistinctBy(f => f.RelativePath).SelectManyAwait(async i => (i.Results ?? [i]).ToAsyncEnumerable()), token, async (entry, token) =>
                {
                    string result = "Succeeded";
                    try
                    {
                        Console.WriteLine($"[{stageName}] Start {entry.RelativePath}");
                        entry = entry with
                        {
                            Source = entry.Target,
                            Target = Path.GetFullPath(Path.Combine(root, entry.RelativePath))
                        };
                        var marker = entry.Target + ".marker";
                        CreateDirectoryForFile(marker);

                        preRun?.Invoke(entry, token);

                        if (force || !Exists(marker) || new FileInfo(marker).Length == 0)
                        {
                            if (!isRunning)
                            {
                                result = "Skipping";
                                return;
                            }

                            await runAsync.Invoke(entry, token);

                            if (!force)
                            {
                                File.WriteAllText(marker, JsonSerializer.Serialize(
                                    new MarkerData()));
                            }
                        }
                        else if (readMarker)
                        {
                            var markerData = JsonSerializer.Deserialize<MarkerData>(Out.Var(out var mt, File.ReadAllText(marker)));
                            result = "Up to date";

                            if (RestoreState)
                            {
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
                        Console.WriteLine($"[{stageName}] End {entry.RelativePath}. Result = {result}");
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

    public record FileEntry(string Origin, string RelativePath, string Source, string Target)
    {
        public int? SegmentCount;

        public string Target { get; set; } = Target;

        public YoutubeFile File = default!;
        public FileEntry[]? Results = null;
    }

    public record MarkerData()
    {
        public Timestamp Timestamp { get; set; } = Timestamp.Now;
    }

    private bool Exists(string splitMarker) => File.Exists(splitMarker);
}