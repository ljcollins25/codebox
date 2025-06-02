using System.Collections.ObjectModel;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using Azure.Storage.Blobs.Specialized;
using FluentAssertions;

namespace Nexis.Azure.Utilities.Tests;


public abstract partial class TestBase
{
    public virtual Url ContainerUriWus2 { get; }
    public virtual Url ContainerUriWus { get; }
    public virtual Url ContainerUriJpe { get; }

    public IConsole TestConsole { get; } = new SystemConsole();

    public CancellationToken Token { get; } = CancellationToken.None;
}

public partial class CliTests : TestBase
{
    [Fact]
    public async Task TestTranslate()
    {
        var op = new TranslateOperation(TestConsole, Token)
        {
            //AudioFile = "https://drive.google.com/file/d/11FrHoYSLuRfCcaDQvGqVYjYbxnlLneM-/view?filename=%5B%5Bhello%5D%5D",
            AudioFile = @"C:\mount\outputs\split\hello.mp4",
            GdrivePath = $"gdrive:heygen/staging/{Environment.MachineName}.mp4",
            //AudioFile = @"C:\mount\YellowBoots.S01E72.trimmed-6e40ab2a-000.mp4",

            Languages = [eng, jpn, zho],
            DryRun = true
        };

        await op.RunAsync();
    }

    [Fact]
    public async Task TestTransform()
    {
        var op = new TransformFiles(TestConsole, Token)
        {
            LocalSourcePath = @"C:\mount\mediawus2",
            RelativePath = @"C:\mount\mediawus2\Media\TV Shows\Dear Heaven {tvdb-282733}\Season 01\Dear Heaven - S01E01 - Episode 1.mp4",
            CompletedTranslationFolder = @"C:\mount\mediawus\translations\completed",
            GdrivePath = $"gdrive:heygen/staging/{Environment.MachineName}.mp4",
            Languages = [eng, jpn, zho],
            OutputRoot = @"C:\mount\outputs",

        };

        await op.RunAsync();
    }


    [Fact]
    public async Task TestSplit()
    {
        var op = new SplitAudio(TestConsole, Token)
        {
            VideoFile = @"C:\mount\mediawus2\Media\TV Shows\Fall In Love {tmdb-130652}\Season 01\Fall In Love - S01E01 - Episode 1.mp4",
            OutputFolder = @"C:\mount\outputs\stage"
        };

        await op.RunAsync();
    }


    [Fact]
    public async Task TestMergeAudio()
    {
        var op = new MergeAudio(TestConsole, Token)
        {
            OutputAudioFile = @"C:\mount\outputs\stage\merge.m4a",
            InputFolder = @"C:\mount\outputs\stage"
        };

        await op.RunAsync();
    }


    [Fact]
    public async Task TestDownload()
    {
        var op = new DownloadTranslation(TestConsole, Token)
        {
            VideoId = "c4962cbfda574318817cdae2dad4d661",
            TargetFolder = @"C:\mount\outputs\YellowBoots.S01E72.1",
            Language = eng,
            Delete = true,
            Download = false
        };

        await op.RunAsync();
    }

    [Fact]
    public async Task TestNaming()
    {
        var name = ExtractFilenameFromContentDispositionUrl("https://resource2.heygen.ai/video_translate/033b4125bdcb4018983381d7f83f0f5b/640x360.mp4?response-content-disposition=attachment%3B+filename%2A%3DUTF-8%27%27https%253A%2F%2Fdrive.google.com%2Ffile%2Fd%2F10yw4foYNUqjph0YwC8_0TkjCJMB1--KX%2Fview%253Ffilename%253D%25255B%25255B6961efd570f74c7d949a54fbad803beb-000%25255D%25255D.m4a.mp4.mp4%3B");
    }

    [Fact]
    public async Task TestHelp()
    {
        Console.SetError(Console.Out);
        //await Program.RunAsync(new Program.Args(
        //    "dehydrate",
        //    "--uri", ContainerUriWus2
        //    //, "--expiry", "0"
        //    //, "--refresh-interval", "5d"
        //));

        var path = @"C:\mount\mediawus2\Media\TV Shows\Fall In Love {tmdb-130652}\tvshow.nfo";
        var root = string.Join("\\", path.Split("\\")[..3]);
        //var relativeUri = string.Join('/', Path.GetDirectoryName(path)!.Split('\\')[3..]);
        await Program.RunAsync(new Program.Args(
            "upload",
            "--uri", ContainerUriWus,//.Combine(relativeUri),
            "--path", root,
            "--relative", path
        //, "--expiry", "0"
        //, "--refresh-interval", "5d"
        ));
    }
}