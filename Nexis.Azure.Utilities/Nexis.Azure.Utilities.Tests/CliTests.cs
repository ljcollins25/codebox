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
    public virtual string ContainerUriWus2 { get; }
}

public partial class CliTests : CliTestsBase
{
    [Fact]
    public async Task TestHelp()
    {
        Console.SetError(Console.Out);
        await Program.RunAsync(new Program.Args(
            "dehydrate",
            "--uri", ContainerUriWus2
            , "--expiry", "0"
            //, "--refresh-interval", "5d"
        ));
    }
}