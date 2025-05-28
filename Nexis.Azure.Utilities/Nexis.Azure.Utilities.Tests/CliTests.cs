using System.Collections.ObjectModel;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using Azure.Storage.Blobs.Specialized;
using FluentAssertions;

namespace Nexis.Azure.Utilities.Tests;


public abstract partial class CliTestsBase
{
    public virtual Url ContainerUriWus2 { get; }
    public virtual Url ContainerUriWus { get; }
    public virtual Url ContainerUriJpe { get; }
}

public partial class CliTests : CliTestsBase
{
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
        var relativeUri = string.Join('/', Path.GetDirectoryName(path)!.Split('\\')[3..]);
        await Program.RunAsync(new Program.Args(
            "upload",
            "--uri", ContainerUriJpe.Combine(relativeUri),
            "--path", path
        //, "--expiry", "0"
        //, "--refresh-interval", "5d"
        ));
    }
}