using System.Collections.ObjectModel;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using Azure.Storage.Blobs.Specialized;
using FluentAssertions;

namespace Nexis.Azure.Utilities.Tests;

public partial class CliTests
{
    public string ShareUriWus2 { get; } = "";
    public string ContainerUriWus2 { get; } = "";

    [Fact]
    public async Task TestHelp()
    {
        Console.SetError(Console.Out);
        await Program.RunAsync(new Program.Args(
            "dehydrate",
            "--share-uri",
            ShareUriWus2,
            "--container-uri",
            ContainerUriWus2
        ));
    }
}