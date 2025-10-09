using System.Collections.ObjectModel;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text.Json;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using YamlDotNet.Serialization;

namespace Nexis.Azure.Utilities.Tests;


public abstract partial class TestBase
{
    public virtual Url ContainerUriWus2 { get; }
    public virtual Url ContainerUriWus { get; }
    public virtual Url ContainerUriJpe { get; }
    public virtual Url ContainerUriJpe_Files { get; }

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
    public async Task TestDehydrate()
    {
        var suffix = "Media/Anime/As a Reincarnated Aristocrat, I'll Use My Appraisal Skill to Rise in the World {tmdb-237150}/";
        var op = new DehydrateOperation(TestConsole, Token)
        {
            DryRun = true,
            DirectoryUri = ContainerUriJpe_Files,//.Combine(suffix),
            Uri = ContainerUriJpe,//.Combine(suffix),
            MinDehydrationSize = 2_000_000,
            RefreshInterval = ParseTimeSpan("3d"),
            Expiry = ParsePastDateTimeOffset("20m"),
        };

        await op.RunAsync();
    }

    [Fact]
    public async Task TranslatePlaylist()
    {
        //var titles = string.Join("\n", )
    }

    [Fact]
    public async Task ConvertPlaylist()
    {
        var path = @"C:\mount\youtube\Drama.json";
        var text = "{}" ?? File.ReadAllText(path);
        var files = YoutubePlaylist.ReadFromVideoData(text);
        File.WriteAllLines(Path.ChangeExtension(path, ".jsonl"), files.Select(e => JsonSerializer.Serialize(e)));
        File.WriteAllText(Path.ChangeExtension(path, ".yaml"), new Serializer().Serialize(files));

        File.WriteAllLines(Path.ChangeExtension(path, "titles.txt"), files.Select(f => f.Title));

        var titles = files.Take(20).Select(f => f.Title).ToArray();

        var url = "https://hooks.zapier.com/hooks/catch/15677350/uoxtvz4/";

        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("target-path", $"test.json");
        var response = await client.PostAsync(url, new StringContent(string.Join("\n", titles)));
        var content = await response.Content.ReadAsStringAsync();
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

    [Fact]
    public async Task PrintBlobs()
    {
        var token = Token;
        var op = new DriveOperationBase(TestConsole, Token)
        {
            Uri = ContainerUriJpe
        };

        BlobContainerClient targetBlobContainer = op.GetTargetContainerAndPrefix(out var prefix);

        var targetBlobs = await targetBlobContainer.GetBlobsAsync(BlobTraits.Metadata | BlobTraits.Tags, prefix: prefix, cancellationToken: token)
            .OrderBy(b => b.Name)
            .ToListAsync();

        var entries = FilterDirectories(targetBlobs).Select(b => BlobDataEntry.From(b)).ToList();


        File.WriteAllLines(@"C:\mount\mediajpe.jsonl", entries.Select(e => JsonSerializer.Serialize(e)));
    }

    [Fact]
    public async Task DeleteBlobs()
    {
        var token = Token;
        var op = new DeleteFilesOperation(TestConsole, Token)
        {
            Uri = ContainerUriJpe.Combine("MediaSource/Backup"),
        };

        await op.RunAsync();
    }


    [Theory]
    [InlineData(0)]
    [InlineData(0,1, "https://youtu.be/3CgorMd6gOU?si=ZxEafA8zSRA6bmCh")]
    [InlineData(0, 12)]
    [InlineData(0, 200, "Drama")]
    [InlineData(0, 50, "Favs")]
    [InlineData(0, 200, "WatchLater")]
    [InlineData(0, 50, "WatchLater")]
    [InlineData(0, 100, "WatchLater")]
    [InlineData(0, 1, "WatchLater")]
    [InlineData(0, 20, "Travel")]
    public async Task TestYoutubeFlow(int skip = 0, int limit = 10_000, string source = "https://www.youtube.com/watch?v=KxXHrgSIApw")
    {

        var op = new YoutubeDownloadFlow(TestConsole, Token)
        {
            UploadUri = ContainerUriJpe.Combine("Media/Youtube"),
            CookiesFilePath = @"C:\mount\youtube\cookies.txt",
            Sources = [source],
            PlaylistMapPath = @"Q:\src\codebox\scripts\playlists.json",
            GdrivePath = $"gdrive:translatedtitles/",
            OutputRoot = @"Q:\media\youtube",
            Limit = limit,
            Skip = skip,
            RefreshPlaylists = true
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
            Languages = skip >= 62 ? [jpn, eng] : [eng, jpn, zho],
            OutputRoot = @"Q:\mediaoutputs",
            Limit = limit,
            Skip = skip
        };

        await op.RunAsync();
    }

    [Theory]
    [InlineData(@"Media\TV Shows\The Double {tmdb-236033}\Season 01", 10, null, 2, jpn | kor)]
    [InlineData(@"Media\TV Shows\Yellow Boots {tmdb-46542}\Season 01", 54, null, 1)]
    [InlineData(@"Media\TV Shows\Yellow Boots {tmdb-46542}\Season 01", 74, null, 15)]
    [InlineData(@"Media\TV Shows\Dear Heaven {tvdb-282733}\Season 01", 54, null, 16)]
    public async Task TestTransformShows(string showPath, int skip, string? browserPath, int limit = 5, LanguageCode languageFlags = jpn | zho)
    {
        BrowserOperationBase.BrowserProcessPath.Value = browserPath;

        var languages = Enum.GetValues<LanguageCode>().Where(l => languageFlags.HasFlag(l)).ToArray();
        var op = new TransformFiles(TestConsole, Token)
        {
            UploadUri = ContainerUriJpe,
            LocalSourcePath = @"C:\mount\mediajpe",
            RelativePath = @$"C:\mount\mediajpe\{showPath}",
            //CompletedTranslationFolder = @"C:\mount\mediawus\translations\completed",
            CompletedTranslationFolder = @">&=IGNORED",
            GdrivePath = $"gdrive:heygen/staging/{Environment.MachineName}/",
            Languages = languages,
            OutputRoot = @"Q:\mediaoutputs",
            Limit = limit,
            Skip = skip
        };

        await op.RunAsync();
    }

    [Fact]
    public void AlterDirectoryTimestamps()
    {
        var path = @"C:\mount\mediajpe\Media\Youtube";
        var files = new DirectoryInfo(path).GetFiles("*", SearchOption.AllDirectories);

        foreach (var group in files.GroupBy(f => f.DirectoryName))
        {
            var directory = group.First().Directory;
            var lastWriteTime = group.Select(f => f.LastWriteTimeUtc).Max();
            if (directory!.LastWriteTimeUtc != lastWriteTime)
            {
                directory.LastWriteTimeUtc = lastWriteTime;
            }
        }
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
    public async Task TestSummary()
    {
        var client = new HttpClient();

        var result = await client.PostTextAsync(SummarizeUrl, """
            - $ From Scorned to FEARED! 🌹 I Married the Paralyzed Prince ♿️ Tamed His Kids, Crushed My Rivals...
            - $ The four handsome men in front of me are all my brothers. #Revenge #Shortplay #HotShortplay
            - $ [Full Ending] She was a dazzlingly talented and beautiful consort, but in reality, she was a "black lotus" kept by her enemy. That night, the rebellious prince willingly bowed to her, and from then on, they joined hands in revenge and rebirth, bestowing upon her a lifetime of honor and favor! #Costume Romance #RebirthRevenge #Spreading Wings
            - $ [Full Ending] Accidentally reborn as an abandoned consort in the cold palace, she prepared to tread cautiously within the palace, but unexpectedly, the current Queen Mother turned out to be her best friend! Help her gain the emperor's favor and win his heart. She was rescued from the cold palace by the duplicitous and arrogant emperor and became the most favored empress in the six harems! #Costume Romance #Time Travel
            - $ General’s daughter, familyless and blind, met a ruthless prince. She gambled her marriage!
            - $ Falsely accused and poisoned, she’s cast out—saved by the crown prince, she rises as his queen.
            - $ Ignored for three years, consort forced out—she remarried a mighty lord, crushed her ex-husband to regret!
            - $ Loved him for three years, only to be abandoned! Leave in despair, yet was doted on by the crown prince.
            """);

        output.WriteLine(result);
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