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
using System.Text;
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
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;

namespace Nexis.Azure.Utilities;

public record class MergeAudio(IConsole Console, CancellationToken token)
{
    public required string OutputAudioFile;

    public required string InputFolder;

    public TimeSpan SegmentDuration = SplitAudio.DefaultSegmentDuration;

    public bool MergeSubtitles = true;

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OutputAudioFile)!);

        var files = Directory.GetFiles(InputFolder, "*.mp4").OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        var fileList = Path.Combine(InputFolder, "files.txt");
        File.WriteAllLines(fileList, files.Select(f => $"file '{f}'"));

        await ExecAsync("ffmpeg",
            $"""-f concat -safe 0 -i "{fileList}" -vn -c:a libopus "{OutputAudioFile}" """.SplitArgs());

        if (MergeSubtitles)
        {
            MergeSubs();
        }

        return 0;
    }

    public void MergeSubs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OutputAudioFile)!);

        var merged = new Subtitle();

        var subFiles = Directory.GetFiles(InputFolder, "*.ass").OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        var subContent = subFiles.Select(f => Subtitle.Parse(f, Encoding.UTF8)).ToArray();


        for (int i = 0; i < subContent.Length; i++)
        {
            var sub = subContent[i];

            var offset = SegmentDuration.Multiply(i);
            sub.AddTimeToAllParagraphs(offset);


            foreach (var p in sub.Paragraphs)
            {
                p.Text = Nikse.SubtitleEdit.Core.Common.Utilities.RemoveSsaTags(p.Text);
                merged.Paragraphs.Add(p);
            }
        }

        var output = new SubRip().ToText(merged, string.Empty);
        File.WriteAllText(Path.ChangeExtension(OutputAudioFile, ".srt"), output);
    }
}