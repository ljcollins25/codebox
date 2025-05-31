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

public record class MergeAudio(IConsole Console, CancellationToken token)
{
    public required string OutputAudioFile;

    public required string InputFolder;

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OutputAudioFile)!);

        var files = Directory.GetFiles(InputFolder, "*.mp4");
        var fileList = Path.Combine(InputFolder, "files.txt");
        File.WriteAllLines(fileList, files.Select(f => $"file '{f}'"));

        await ExecAsync("ffmpeg",
            $"""-f concat -safe 0 -i "{fileList}" -vn -c:a copy "{OutputAudioFile}" """.SplitArgs());

        return 0;
    }
}