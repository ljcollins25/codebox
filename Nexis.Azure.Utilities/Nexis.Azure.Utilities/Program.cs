using System;
using System.Linq;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using Azure.Storage.Sas;

namespace Nexis.Azure.Utilities;

using static Helpers;
using static Parsed;

public class Program
{
    public static Task<int> Main(params string[] args)
    {
        return RunAsync(args);
    }

    public record struct Args(params string[] Arguments)
    {
        public bool UseExceptionHandler { get; set; } = true;

        public static implicit operator Args(string[] args) => new(args);

        public static implicit operator string[](Args args) => args.Arguments;

        public SubProcessRunner? ToRunner(CancellationToken token) => Arguments.Length == 0 ? null : SubProcessRunner.FromRemainingArgs(Arguments, token);
    }

    public static async Task<int> RunAsync(Args args)
    {
        using var cts = new CancellationTokenSource();
        Environment.SetEnvironmentVariable("AppBaseDirectory", AppContext.BaseDirectory);
        var parseResult = ParseArguments(args, cts);
        return await parseResult.InvokeAsync();
    }

    public static ParseResult ParseArguments(Args args, CancellationTokenSource? cts = null)
    {
        cts ??= new();
        args = FilterArgs(args);
        args = args.Arguments.Select(a => Environment.ExpandEnvironmentVariables(Helpers.ExpandVariables(a))).ToArray();

        var precedingArgs = new List<string>();
        var remainingArgs = new List<string>();

        var list = precedingArgs;
        foreach (var arg in args.Arguments)
        {
            if (arg == "--" && list != remainingArgs)
            {
                list = remainingArgs;
            }
            else
            {
                list.Add(arg);
            }
        }

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        RootCommand rootCommand = GetCommand(cts, new Args(remainingArgs.ToArray()));

        var builder = new CommandLineBuilder(rootCommand)
            .UseVersionOption()
            .UseHelp()
            .UseEnvironmentVariableDirective()
            .UseParseDirective()
            .UseParseErrorReporting();

        if (args.UseExceptionHandler)
        {
            builder = builder.UseExceptionHandler();
        }

        builder = builder.CancelOnProcessTermination();

        return builder.Build().Parse(precedingArgs.ToArray());
    }

    private static string[] FilterArgs(string[] args)
    {
        if (args.Contains("----"))
        {
            var index = args.AsSpan().LastIndexOf("----");
            return args.AsSpan().Slice(index + 1).ToArray();
        }
        else if (args.Contains("{{{{") && args.Contains("}}}}"))
        {
            var span = args.AsSpan();
            var startIndex = span.IndexOf("{{{{") + 1;
            var endIndex = span.IndexOf("}}}}");
            return args.AsSpan()[startIndex..endIndex].ToArray();
        }

        return args;
    }

    private class TestOperation
    {
        public int Port;

        public bool BoolArg = true;

        public Uri? UriArg;

        public required List<string> Args;

        public async Task<int> RunAsync()
        {
            return 0;
        }
    }

    public static RootCommand GetCommand(CancellationTokenSource? cts = null, Args? additionalArgs = null)
    {
        cts ??= new();
        return new RootCommand
        {
            GetStorageCommand(),

            // Add DehydrateOperation command
            CliModel.Bind<UploadFilesOperation>(
                new Command("upload", "Dehydrate Azure Files to Azure Blob Storage."),
                m =>
                {
                    var r = new UploadFilesOperation(m.Console, cts.Token)
                    {
                        Uri = m.Option(c => ref c.Uri, name: "uri", description: "Container sas directory uri", required: true, aliases: ["container-uri"]),
                        LocalSourcePath = m.Option(c => ref c.LocalSourcePath, "path", "Source file/directory path", required: true)
                    };

                    m.Option(c => ref c.ThreadCount, name: "threads", description: "The thread count", defaultValue: r.ThreadCount);
                    m.Option(c => ref c.BlockSizeMb, name: "block-size-mb", description: "The block size (in mb)", defaultValue: r.BlockSizeMb);
                    m.Option(c => ref c.UpdateTimestamps, name: "timestamps", description: "Update timestamps.", defaultValue: r.UpdateTimestamps);
                    m.Option(c => ref c.RelativePath, name: "relative-path", description: "Relative path to upload files from", aliases: ["relative", "rel"]);

                    return r;
                },
                r => r.RunAsync()),
            CliModel.Bind<YoutubeDownloadFlow>(
                new Command("youtube", "Downloader for youtube playlist or videos."),
                m =>
                {
                    var r = new YoutubeDownloadFlow(m.Console, cts.Token)
                    {
                        UploadUri = m.Option(c => ref c.UploadUri, name: "uri", description: "Container sas directory uri", required: true, aliases: ["upload-uri"]),
                        CookiesFilePath = m.Option(c => ref c.CookiesFilePath, "cookies", "The path to the cookies file", required: true),
                        Sources = m.Option(c => ref c.Sources, "source", "A source (playlist or video url)", required: true, aliases: ["s"]),
                        OutputRoot = m.Option(c => ref c.OutputRoot, "output", "The local path to output data", required: true, aliases: ["o"]),
                        GdrivePath = m.Option(c => ref c.GdrivePath, "gdrive-path", "The gdrive path to where translated files go", required: true, defaultValue: $"gdrive:translatedtitles/"),
                    };

                    m.Option(c => ref c.RefreshPlaylists, name: "refresh-playlists", description: "Force download of playlists");
                    m.Option(c => ref c.PlaylistMapPath, name: "playlist-map", description: "The playlist map path");
                    m.Option(c => ref c.ExcludeStages, name: "exclude-stages", description: "Stages to exclude");
                    m.Option(c => ref c.Limit, name: "limit", description: "The limit of number of files to download");
                    m.Option(c => ref c.Skip, name: "skip", description: "The number of files in sequence to skip");

                    return r;
                },
                r => r.RunAsync()),

            CliModel.Bind<DeleteFilesOperation>(
                new Command("rmdir", "Dehydrate Azure Files to Azure Blob Storage."),
                m =>
                {
                    var result = new DeleteFilesOperation(m.Console, cts.Token)
                    {
                        Uri = m.Option(c => ref c.Uri, name: "uri", description: "Container sas uri", required: true, aliases: ["container-uri"]),
                    };

                    m.Option(c => ref c.DryRun, name: "dry-run",
                        description: "Indicates to only run a dry run of the delete", defaultValue: true);
                    return result;
                },
                r => r.RunAsync()),

            CliModel.Bind<DehydrateOperation>(
                new Command("dehydrate", "Dehydrate Azure Files to Azure Blob Storage."),
                m =>
                {
                    var result = new DehydrateOperation(m.Console, cts.Token)
                    {
                        Uri = m.Option(c => ref c.Uri, name: "uri", description: "Container sas uri", required: true, aliases: ["container-uri"]),
                        Expiry = m.ParsedOption(c => ref c.Expiry, ParsePastDateTimeOffset, v => m.Option(c => ref v.Text, name: "expiry", description: "Expiry value (e.g. 1h, 2d)",
                            defaultValue: "1h", required: true)),
                    };

                    m.ParsedOption(c => ref c.EphemeralSnapshotDeleteDelay, ParseTimeSpan, v => m.Option(c => ref v.Text, name: "stage-delete-delay",
                        description: "Time to wait before deleting staging snapshots", defaultValue: "5m"));
                    m.ParsedOption(c => ref c.RefreshInterval, ParseTimeSpan, v => m.Option(c => ref v.Text, name: "refresh-interval",
                        description: "Refresh interval", defaultValue: "5d"));
                    //m.Option(c => ref c.Force, name: "force", description: "Force ghosting files which are up to date.", defaultValue: false);
                    m.Option(c => ref c.RefreshBatches, name: "refresh-batches",
                        description: "Number of refresh batches", defaultValue: 5);
                    m.Option(c => ref c.MinDehydrationSize, name: "min-dehydrate-size",
                        description: "Minimum size of files which are dehydrated");
                    return result;
                },
                r => r.RunAsync()),

            //CliModel.Bind<RunOperation>(
            //    new Command("run", "Run command."),
            //    m =>
            //    {
            //        var result = new RunOperation(additionalArgs?.ToRunner(cts.Token));

            //        m.Option(c => ref c.RetryCount, name: "retries", defaultValue: result.RetryCount);

            //        return result;
            //    },
            //    r => r.RunAsync()),
        };
    }

    public static Command GetStorageCommand()
    {
        var common = CliModel.Bind<SasCommonArguments>(
            new Command("common"),
            m =>
            {
                var result = new SasCommonArguments()
                {
                    Permissions = m.Option(c => ref c.Permissions, name: "permissions", description: "The sas permission (i.e. rwdl).", required: true),
                    AccountName = m.Option(c => ref c.AccountName, name: "account-name", description: "The account name.", required: true),
                    AccountKey = m.Option(c => ref c.AccountKey, name: "account-key", description: "The account key.", required: true),
                    ExpiryValue = m.Option(c => ref c.ExpiryValue, name: "expiry", description: "The sas expiry time.", required: true),
                    Output = m.Option(c => ref c.Output, name: "output", defaultValue: "tsv"),
                    EmitFullUri = m.Option(c => ref c.EmitFullUri, name: "full", defaultValue: false),
                };

                return result;
            },
            r => Task.FromResult(0));

        return new Command("storage")
        {
            //CliModel.Bind<UploadOperation>(
            //    new Command("uploadfile"),
            //    m =>
            //    {
            //        var result = new UploadOperation(m.Console, m.Token)
            //        {
            //            Source = m.Option(c => ref c.Source, name: "source", description: "The source file path", required: true),
            //            TargetUri = m.Option(c => ref c.TargetUri, name: "target", description: "The target blob uri", required: true),
            //        };

            //        m.Option(c => ref c.Overwrite, name: "overwrite", defaultValue: false);

            //        return result;
            //    },
            //    r => r.RunAsync()),

            new Command("account")
            {
                CliModel.Bind<AccountSasArguments>(
                    new Command("generate-sas"),
                    m =>
                    {
                        var result = new AccountSasArguments(m.Console)
                        {
                            CommonArguments = m.SharedOptions(c => ref c.CommonArguments, common),
                            Services = m.Option(c => ref c.Services, name: "services", required: true),
                            ResourceTypes = m.Option(c => ref c.ResourceTypes, name: "resource-types", required: true),
                        };

                        return result;
                    },
                    r => r.RunAsync()),
            },
            new[] { true, false }.Select(isBlob =>
            {
                return new Command(isBlob ? "blob" : "container")
                {
                    CliModel.Bind<BlobOrContainerArguments>(
                        new Command("generate-sas"),
                        m =>
                        {
                            var result = new BlobOrContainerArguments(m.Console)
                            {
                                CommonArguments = m.SharedOptions(c => ref c.CommonArguments, common),
                                ContainerName = m.Option(c => ref c.ContainerName, name: isBlob ? "container-name" : "name", description: "The container name.", required: true),
                                BlobName = m.Option(c => ref c.BlobName, name: isBlob ? "name" : "blob-name", description: "The blob name.", required: isBlob)
                            };

                            return result;
                        },
                        r => r.RunAsync())
                };
            })
        };
    }
}