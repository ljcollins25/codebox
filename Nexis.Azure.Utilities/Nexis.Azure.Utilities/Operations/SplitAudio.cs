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

    public Vuid? OperationId;

    public static TimeSpan DefaultSegmentDuration = TimeSpan.FromMinutes(25);

    public TimeSpan SegmentDuration = DefaultSegmentDuration;

    public string VideoSize { get; set; } = "640x360";

    public async Task<int> RunAsync()
    {
        OperationId ??= Vuid.FromFileName(VideoFile);

        var intermediateFolder = Path.Combine(OutputFolder, "extracted");
        Directory.CreateDirectory(OutputFolder);
        
        if (Directory.Exists(intermediateFolder)) Directory.Delete(intermediateFolder, true);
        Directory.CreateDirectory(intermediateFolder);
        var intermediateVideoFile = VideoFile;
        //intermediateVideoFile = Path.Combine(intermediateFolder, Path.GetFileName(VideoFile));
        //File.Copy(VideoFile, intermediateVideoFile);
        //using var videoFs = new FileStream(intermediateVideoFile, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 12, FileOptions.DeleteOnClose);
        //using (var sourceVideoFs = File.Open(VideoFile, FileMode.Open))
        //{
        //    sourceVideoFs.CopyTo(videoFs, 1 << 20);
        //}

        var audioExt = Path.GetExtension(VideoFile).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            ? "m4a"
            : "mp3";

        var opId = $"{OperationId:n}";
        string tempAudioPattern = Path.Combine(intermediateFolder, $"[[{opId}-%03d]].{audioExt}");

        var duration = ((int)SegmentDuration.TotalSeconds).ToString();
        var result = await ExecAsync("ffmpeg",
        [
            "-y", "-nostdin",
            "-i", intermediateVideoFile,
            "-f", "segment",
            "-segment_time", duration,
            "-vn",
            "-c:a", "copy",
            tempAudioPattern
        ],
        isCliWrap: true);

        var audioFiles = Directory.GetFiles(intermediateFolder)
            .Where(f => f.Contains(opId));

        foreach (var audioFile in audioFiles)
        {
            var videoWrappedAudioFile = Path.Combine(OutputFolder, Path.GetFileName(audioFile) + ".mp4");
            await ExecAsync("ffmpeg",
            [
                "-f", "lavfi",
                "-i", $"color=size={VideoSize}:rate=30:duration={duration}",
                "-i", audioFile,
                "-map", "1:a",
                "-map", "0:v",
                "-shortest",
                "-c:v", "libx264",
                "-preset", "ultrafast",
                "-crf", "30",
                "-c:a", "copy",
                videoWrappedAudioFile
            ],
            isCliWrap: true);
        }

        return 0;
    }
}