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
            AudioFile = @"C:\mount\YellowBoots.S01E72.trimmed-6e40ab2a-000.mp4",
            Languages = [eng, jpn, zho]
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
            Language = eng
        };

        await op.RunAsync();
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