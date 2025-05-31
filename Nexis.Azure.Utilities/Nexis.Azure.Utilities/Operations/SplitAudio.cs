using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using DotNext.IO;
using Microsoft.Playwright;

namespace Nexis.Azure.Utilities;

public record class SplitAudio(IConsole Console, CancellationToken token)
{
    public required string VideoFile;

    public required string OutputFolder;

    public Guid OperationId = Guid.NewGuid();

    public TimeSpan SegmentDuration = TimeSpan.FromMinutes(25);

    public string VideoSize { get; set; } = "640x360";

    public async Task<int> RunAsync()
    {
        var intermediateFolder = Path.Combine(OutputFolder, "extracted");
        Directory.CreateDirectory(OutputFolder);
        Directory.CreateDirectory(intermediateFolder);

        var audioExt = Path.GetExtension(VideoFile).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            ? "m4a"
            : "mp3";

        string tempAudioPattern = Path.Combine(intermediateFolder, $"[[{OperationId:n}]].audio-%03d.{audioExt}");

        var result = await ExecAsync("ffmpeg",
        [
            "-i", VideoFile,
            "-f", "segment",
            "-segment_time", ((int)SegmentDuration.TotalSeconds).ToString(),
            "-vn",
            "-acodec", "aac",
            tempAudioPattern
        ]);

        var audioFiles = Directory.GetFiles(intermediateFolder);

        foreach (var audioFile in audioFiles)
        {
            var videoWrappedAudioFile = audioFile + ".mp4";
            await ExecAsync("ffmpeg",
            [
                "-f", "lavfi",
                "-i", $"color=size={VideoSize}:rate=30:duration={SegmentDuration}",
                "-i", audioFile,
                "-map", "1:a",
                "-map", "0:v",
                "-shortest",
                "-c:v", "libx264",
                "-preset", "ultrafast",
                "-crf", "30",
                "-c:a", "copy",
                videoWrappedAudioFile
            ]);
        }

        return 0;
    }
}