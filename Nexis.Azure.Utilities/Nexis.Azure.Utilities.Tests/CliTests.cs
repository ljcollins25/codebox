using System.Collections.ObjectModel;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text.Json;
using Azure.Storage.Blobs.Specialized;
using FluentAssertions;
using Xunit.Abstractions;

namespace Nexis.Azure.Utilities.Tests;


public abstract partial class TestBase
{
    public virtual Url ContainerUriWus2 { get; }
    public virtual Url ContainerUriWus { get; }
    public virtual Url ContainerUriJpe { get; }

    public IConsole TestConsole { get; }

    public CancellationToken Token { get; } = CancellationToken.None;

    public TestBase(ITestOutputHelper output)
    {
        Console.SetError(new TestOutputWriter(output));
        Console.SetOut(new TestOutputWriter(output));
        TestConsole = new OutputConsole();
    }


    private class OutputConsole : TestConsole
    {
        public OutputConsole()
        {
            Out = StandardStreamWriter.Create(System.Console.Out);
            Error = StandardStreamWriter.Create(System.Console.Error);
            IsErrorRedirected = true;
            IsOutputRedirected = true;
        }
    }
}

public partial class CliTests(ITestOutputHelper output) : TestBase(output)
{
    [Fact]
    public async Task TestTranslate()
    {
        var op = new TranslateOperation(TestConsole, Token)
        {
            AudioFile = "https://drive.google.com/file/d/11FrHoYSLuRfCcaDQvGqVYjYbxnlLneM-/view?filename=%5B%5Bhello%5D%5D",
            //AudioFile = @"C:\mount\outputs\split\hello.mp4",
            //GdrivePath = $"gdrive:heygen/staging/{Environment.MachineName}.mp4",
            //AudioFile = @"C:\mount\YellowBoots.S01E72.trimmed-6e40ab2a-000.mp4",

            Languages = [zho],
            ApiMode = false,
            DryRun = true
        };

        await op.RunAsync();
    }


    [Fact]
    public async Task GetCookies()
    {
        var op = new GetYoutubeCookies(TestConsole, Token)
        {
            TargetFile = @"C:\mount\youtube\cookies.txt"
        };

        await op.RunAsync();
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(45, null, 10)]
    [InlineData(51, null, 5)]
    [InlineData(60, null, 2)]
    [InlineData(61, null, 20)]
    [InlineData(62, null, 20)]
    [InlineData(36, null)]
    [InlineData(36, @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe")]
    [InlineData(67, null, 1)]
    [InlineData(66, @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe", 1)]
    public async Task TestTransform(int skip, string? browserPath, int limit = 5)
    {
        BrowserOperationBase.BrowserProcessPath.Value = browserPath;

        var op = new TransformFiles(TestConsole, Token)
        {
            UploadUri = ContainerUriJpe,
            LocalSourcePath = @"C:\mount\mediajpe",
            RelativePath = @"C:\mount\mediajpe\Media\TV Shows\Dear Heaven {tvdb-282733}\Season 01",
            CompletedTranslationFolder = @"C:\mount\mediawus\translations\completed",
            GdrivePath = $"gdrive:heygen/staging/{Environment.MachineName}/",
            Languages =  skip >= 62 ? [jpn, eng] : [eng, jpn, zho],
            OutputRoot = @"Q:\mediaoutputs",
            Limit = limit,
            Skip = skip
        };

        await op.RunAsync();
    }

    [Theory]
    [InlineData(@"Media\TV Shows\Yellow Boots {tmdb-46542}\Season 01", 54, null, 1)]
    [InlineData(@"Media\TV Shows\Yellow Boots {tmdb-46542}\Season 01", 74, null, 15)]
    public async Task TestTransformShows(string showPath, int skip, string? browserPath, int limit = 5)
    {
        BrowserOperationBase.BrowserProcessPath.Value = browserPath;

        var op = new TransformFiles(TestConsole, Token)
        {
            UploadUri = ContainerUriJpe,
            LocalSourcePath = @"C:\mount\mediajpe",
            RelativePath = @$"C:\mount\mediajpe\{showPath}",
            CompletedTranslationFolder = @"C:\mount\mediawus\translations\completed",
            GdrivePath = $"gdrive:heygen/staging/{Environment.MachineName}/",
            Languages = [jpn, zho],
            OutputRoot = @"Q:\mediaoutputs",
            Limit = limit,
            Skip = skip
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
            VideoId = "6d8f88de98424358ae4ff51a2da05a93",
            TargetFolder = @"C:\mediaoutputs\test",
            Language = eng,
            Delete = false,
            Download = false,
            Prioritize = true
        };

        await op.RunAsync();
    }

    [Fact]
    public async Task TestList()
    {
        var op = new ListVideosOperation(TestConsole, Token)
        {
            MarkerFolder = @"Q:\mediaoutputs\translate",
            Organize = true,
            DryRun = false,
            Print = true
        };

        await op.RunAsync();
    }

    [Fact]
    public async Task TestNaming()
    {

        var name = ExtractFilenameFromContentDispositionUrl("https://resource2.heygen.ai/video_translate/033b4125bdcb4018983381d7f83f0f5b/640x360.mp4?response-content-disposition=attachment%3B+filename%2A%3DUTF-8%27%27https%253A%2F%2Fdrive.google.com%2Ffile%2Fd%2F10yw4foYNUqjph0YwC8_0TkjCJMB1--KX%2Fview%253Ffilename%253D%25255B%25255B6961efd570f74c7d949a54fbad803beb-000%25255D%25255D.m4a.mp4.mp4%3B");

        var fd = ExtractFileDescriptor(name);

        var st = $"{fd:n}";

        var md = new TransformFiles.MarkerData(new("helol"), 23);

        var json = JsonSerializer.Serialize(md);

        var tr = new TranslateRequest(
            name: "",
            google_url: "",
            output_languages: [eng, jpn, zho]);
        var trjson = JsonSerializer.Serialize(tr);



        var rt = JsonSerializer.Deserialize<TransformFiles.MarkerData>(json);
    }

    [Fact]
    public async Task TestId()
    {
        
        var path = @"C:\mount\mediajpe\Media\TV Shows\Dear Heaven {tvdb-282733}\Season 01\Dear Heaven - S01E01 - Episode 1.mp4";
        var id = GetOperationId(path);
    }

    [Fact]
    public async Task TestProc()
    {
        await BrowserOperationBase.EnsureChromiumRunning();

        await Task.Delay(100000);
    }

    [Fact]
    public async Task TestHelp()
    {
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