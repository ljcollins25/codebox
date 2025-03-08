using System;
using System.Linq;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using Azure.Storage.Sas;
using Microsoft.Azure.Pipelines.WebApi;

namespace AzureDevops.Pipeline.Utilities;

using static Helpers;

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
    }

    public static async Task<int> RunAsync(Args args)
    {
        args = FilterArgs(args);
        Environment.SetEnvironmentVariable("AppBaseDirectory", AppContext.BaseDirectory);
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

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var agentRunner = remainingArgs.Count > 0
            ? new SubProcessRunner(remainingArgs[0], remainingArgs.Skip(1), cts.Token)
            : null;

        RootCommand rootCommand = GetCommand(cts, agentRunner);

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

        return await builder.Build().Parse(precedingArgs.ToArray()).InvokeAsync();
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

    public static RootCommand GetCommand(CancellationTokenSource? cts = null, SubProcessRunner? subProcessRunner = null)
    {
        cts ??= new();
        return new RootCommand
        {
            CliModel.Bind<TestOperation>(
                new Command("test") { IsHidden = true },
                m =>
                {
                    var result = new TestOperation()
                    {
                        Args = m.Option(c => ref c.Args, name: "args")
                    };

                    m.Option(c => ref c.UriArg, name: "uri");
                    m.Option(c => ref c.Port, name: "port");
                    m.Option(c => ref c.BoolArg, name: "bool");

                    return result;
                },
                r => r.RunAsync()),

            GetStorageCommand(),

            //CliModel.Bind<WriteOperation>(
            //    new Command("write", "Emits set variable."),
            //    m =>
            //    {
            //        var result = new WriteOperation(m.Console)
            //        {
            //            TaskUrl = m.Option(c => ref c.TaskUrl, name: "taskUrl", required: true,
            //                defaultValue: Env.TaskUri,
            //                description: $"annotated build task uri (e.g. {TaskUriTemplate} )"),
            //            AdoToken = m.Option(c => ref c.AdoToken, name: "token", description: "The access token (e.g. $(System.AccessToken) )", required: true),
            //            Lines = m.Option(c => ref c.Lines, name: "lines", description: "The lines to write", required: true),
            //        };

            //        m.Option(c => ref c.Debug, name: "debug");

            //        return result;
            //    },
            //    r => r.RunAsync()),

             CliModel.Bind<InfoTaskOperation>(
                new Command("info", "Display info."),
                m =>
                {
                    var result = new InfoTaskOperation(m.Console)
                    {
                        TaskUrl = m.Option(c => ref c.TaskUrl, name: "taskUrl", required: true,
                            defaultValue: Env.TaskUri,
                            description: $"annotated build task uri (e.g. {TaskUriTemplate} )"),
                        AdoToken = m.Option(c => ref c.AdoToken, name: "token",
                            defaultValue: Globals.Token,
                            description: "The access token (e.g. $(System.AccessToken) )", required: true),
                    };


                    m.Option(c => ref c.Debug, name: "debug");
                    m.Option(c => ref c.Load, name: "set-context",
                        isExplicitRef: c => ref c.TaskUrlSpecified,
                        description: "Causes task info to be set as variable so that calls to azputils in subsequent tasks use this tasks info");

                    return result;
                },
                r => r.RunAsync()),

            CliModel.Bind<RunTaskCommandOperation>(
                new Command("runtaskcmd", "Run command until it completes or build finishes. Also completes agent invocation task."),
                m =>
                {
                    var result = new RunTaskCommandOperation(m.Console, cts, subProcessRunner)
                    {
                        TaskUrl = m.Option(c => ref c.TaskUrl, name: "taskUrl", required: true,
                            defaultValue: Env.TaskUri,
                            description: $"annotated build task uri (e.g. {TaskUriTemplate} )"),
                        AdoToken = m.Option(c => ref c.AdoToken, name: "token",
                            defaultValue: Globals.Token,
                            description: "The access token (e.g. $(System.AccessToken) )", required: true),
                    };

                    m.Option(c => ref c.PollSeconds, name: "pollSeconds", defaultValue: 5);
                    m.Option(c => ref c.RetryCount, name: "retries", defaultValue: result.RetryCount);
                    m.Option(c => ref c.AgentTimeoutSeconds, name: "timeoutSeconds");
                    m.Option(c => ref c.Debug, name: "debug");

                    return result;
                },
                r => r.RunAsync()),

            CliModel.Bind<RunOperation>(
                new Command("run", "Run command."),
                m =>
                {
                    var result = new RunOperation(subProcessRunner);

                    m.Option(c => ref c.RetryCount, name: "retries", defaultValue: result.RetryCount);

                    return result;
                },
                r => r.RunAsync()),

            CliModel.Bind<RunOperation>(
                new Command("runagent", "Runs azure devops agent."),
                m =>
                {
                    var result = new RunOperation(subProcessRunner);

                    m.Option(c => ref c.RetryCount, name: "retries", defaultValue: result.RetryCount);

                    return result;
                },
                r => r.RunAsync()),

            CliModel.Bind<SynchronizeOperation>(
                new Command("synchronize", "Synchronize following steps in parallel jobs"),
                m =>
                {
                    var result = new SynchronizeOperation(m.Console)
                    {
                        TaskUrl = m.Option(c => ref c.TaskUrl, name: "taskUrl", required: true,
                            defaultValue: Env.TaskUri,
                            description: $"annotated build task uri (e.g. {TaskUriTemplate} )"
                            ),
                        AdoToken = m.Option(c => ref c.AdoToken, name: "token", description: "The access token (e.g. $(System.AccessToken) )", required: true, defaultValue: Globals.Token),
                        JobCount = m.Option(c => ref c.JobCount,
                            name: "jobCount",
                            defaultValue: Env.TotalJobsInPhase,
                            description: "The number of job slots available for synchronization (e.g. $(System.TotalJobsInPhase) )", required: true),
                        CorrelationKey = m.Option(c => ref c.CorrelationKey,
                            name: "key",
                            defaultValue: "",
                            description: "The key used to correlated between multiple synchronizations in the same job phase", required: true),
                        PhaseId = m.Option(c => ref c.PhaseId,
                            name: "phaseId",
                            defaultValue: Env.PhaseId,
                            description: "The phase id (e.g. $(System.PhaseId))", required: true),
                    };

                    m.Option(c => ref c.DisplayName,
                        name: "name",
                        defaultValue: Env.JobDisplayName,
                        description: "The name of the job (e.g. $(System.JobDisplayName) )");
                    m.Option(c => ref c.PollSeconds, name: "pollSeconds", defaultValue: 5);
                    m.Option(c => ref c.Debug, name: "debug");

                    return result;
                },
                r => r.RunAsync()),
            CliModel.Bind<ZipOperation>(
                new Command("gz", "Compresses the given file using gzip to the given path"),
                m =>
                {
                    var result = new ZipOperation(m.Console, m.Token)
                    {
                        Source = m.Option(c => ref c.Source, name: "source", description: "The source file path", required: true),
                        Target = m.Option(c => ref c.Target, name: "target", description: "The target file path", required: true),
                    };

                    m.Option(c => ref c.Overwrite, name: "overwrite", defaultValue: result.Overwrite);
                    m.Option(c => ref c.CompressionLevel, name: "compression-level", defaultValue: result.CompressionLevel);

                    return result;
                },
                r => r.RunAsync()),
            CliModel.Bind<LogExtractOperation>(
                new Command("extract-log", "Extract variables from a log file"),
                m =>
                {
                    var result = new LogExtractOperation(m.Console)
                    {
                        TaskUrl = m.Option(c => ref c.TaskUrl, name: "taskUrl", required: true,
                            defaultValue: Env.TaskUri,
                            description: $"annotated build task uri (e.g. {TaskUriTemplate} )"),
                        AdoToken = m.Option(c => ref c.AdoToken, name: "token", description: "The access token (e.g. $(System.AccessToken) )", required: true),
                        Patterns = m.Option(c => ref c.Patterns, name: "--patterns", description: "The patterns for extraction", required: true),
                    };

                    m.Option(c => ref c.MissingBehavior, name: "missing-behavior", description: "The behavior for missing values", defaultValue: result.MissingBehavior);
                    m.Option(c => ref c.SourceId, name: "source-id", description: "The id of the source task logs to process");
                    m.Option(c => ref c.StartLine, name: "start-line", description: "The start line of the logs");
                    m.Option(c => ref c.EndLine, name: "end-line", description: "The end line of the logs");
                    m.Option(c => ref c.IsSecret, name: "secret");
                    m.Option(c => ref c.IsOutput, name: "output");

                    return result;
                },
                r => r.RunAsync()),

            CliModel.Bind<UpdateRecordOperation>(
                new Command("update-record", "Updates the given timeline record"),
                m =>
                {
                    var result = new UpdateRecordOperation(m.Console)
                    {
                        TaskUrl = m.Option(c => ref c.TaskUrl, name: "taskUrl", required: true,
                            defaultValue: Env.TaskUri,
                            description: $"annotated build task uri (e.g. {TaskUriTemplate} )"),
                        AdoToken = m.Option(c => ref c.AdoToken, name: "token", description: "The access token (e.g. $(System.AccessToken) )", required: true, defaultValue: Globals.Token),
                        Id = m.Option(c => ref c.Id,
                            name: "id",
                            description: "The id of the record"),
                        Name = m.Option(c => ref c.Name, name: "name", description: "The name of the task"),
                        ParentId = m.Option(c => ref c.ParentId,
                            name: "parent-id",
                            description: "The parent id of the created record"),
                        RecordType = m.Option(c => ref c.RecordType, name: "type", description: "The record type"),
                        Result = m.Option(c => ref c.Result, name: "result", description: "The result"),
                        PercentComplete = m.Option(c => ref c.PercentComplete, name: "progress", description: "The percent complete"),
                    };

                    m.Option(c => ref c.SecretVariables, name: "secrets", description: "The secret variable names to upsert");
                    m.Option(c => ref c.Variables, name: "variables", description: "The non-secret variable names to upsert");
                    m.Option(c => ref c.VariableInputPrefix, name: "variable-input-prefix", description: "The prefix of variables to upsert");
                    m.Option(c => ref c.VariableOutputPrefix, name: "variable-output-prefix", description: "The prefix added to upserted variables");

                    m.Option(c => ref c.Debug, name: "debug");

                    return result;
                },
                r => r.RunAsync()),

            CliModel.Bind<DownloadLogsOperation>(
                new Command("download-log", "Downloads the log to a file"),
                m =>
                {
                    var result = new DownloadLogsOperation(m.Console)
                    {
                        TaskUrl = m.Option(c => ref c.TaskUrl, name: "taskUrl", required: true,
                            defaultValue: Env.TaskUri,
                            description: $"annotated build task uri (e.g. {TaskUriTemplate} )"),
                        Target = m.Option(c => ref c.Target, name: "target", description: "The target file or null to output to standard out"),
                        AdoToken = m.Option(c => ref c.AdoToken, name: "token", description: "The access token (e.g. $(System.AccessToken) )", required: true, defaultValue: Globals.Token),
                    };

                    m.Option(c => ref c.HeaderLines, name: "prepend", description: "The header line(s) to add to the log");
                    m.Option(c => ref c.FormatHeaders, name: "format-headers", description: "Causes header lines to be formatted along with log lines", defaultValue: result.FormatHeaders);
                    m.Option(c => ref c.Prefix, name: "prefix", description: "The prefix to add to each line.");
                    m.Option(c => ref c.Prefix, name: "escaping", description: "The escaping rules to apply to the line text");

                    m.Option(c => ref c.Format, name: "format", description: "The format string to replace token in");
                    m.Option(c => ref c.ReplacementToken, name: "replacement-token", description: "The replacement token in the format string", defaultValue: result.ReplacementToken);

                    m.Option(c => ref c.StartLine, name: "start-line", description: "The start line of the logs");
                    m.Option(c => ref c.EndLine, name: "end-line", description: "The end line of the logs");
                    m.Option(c => ref c.StartLinePattern, name: "start-line-pattern", description: "The start line regex pattern of the logs");
                    m.Option(c => ref c.EndLinePattern, name: "end-line-pattern", description: "The end line pattern of the logs");
                    m.Option(c => ref c.Debug, name: "debug");

                    return result;
                },
                r => r.RunAsync()),

            CliModel.Bind<CopyLogOperation>(
                new Command("copy-log", "Copies a reference to the log under another job"),
                m =>
                {
                    var result = new CopyLogOperation(m.Console)
                    {
                        TaskUrl = m.Option(c => ref c.TaskUrl, name: "taskUrl", required: true,
                            defaultValue: Env.TaskUri,
                            description: $"annotated build task uri (e.g. {TaskUriTemplate} )"),
                        AdoToken = m.Option(c => ref c.AdoToken, name: "token", description: "The access token (e.g. $(System.AccessToken) )", required: true, defaultValue: Globals.Token),
                        PhaseId = m.Option(c => ref c.PhaseId,
                            name: "phaseId",
                            defaultValue: Env.PhaseId.ToNullable(),
                            description: "The phase id (e.g. $(System.PhaseId))"),
                        Name = m.Option(c => ref c.Name, name: "name", description: "The name of the task", required: true),
                    };

                    m.Option(c => ref c.HeaderLines, name: "prepend", description: "The header line(s) to add to the log");
                    m.Option(c => ref c.FormatHeaders, name: "format-headers", description: "Causes header lines to be formatted along with log lines", defaultValue: result.FormatHeaders);
                    m.Option(c => ref c.Prefix, name: "prefix", description: "The prefix to add to each line.");
                    m.Option(c => ref c.Prefix, name: "escaping", description: "The escaping rules to apply to the line text");

                    m.Option(c => ref c.Format, name: "format", description: "The format string to replace token in");
                    m.Option(c => ref c.ReplacementToken, name: "replacement-token", description: "The replacement token in the format string", defaultValue: result.ReplacementToken);

                    m.Option(c => ref c.ParentJobName, name: "parent-job-name", description: "The name or id of the parent job");
                    m.Option(c => ref c.StartLine, name: "start-line", description: "The start line of the logs");
                    m.Option(c => ref c.EndLine, name: "end-line", description: "The end line of the logs");
                    m.Option(c => ref c.StartLinePattern, name: "start-line-pattern", description: "The start line regex pattern of the logs");
                    m.Option(c => ref c.EndLinePattern, name: "end-line-pattern", description: "The end line pattern of the logs");
                    m.Option(c => ref c.SourceId, name: "source-id", description: "The id of the source task logs to copy");
                    m.Option(c => ref c.TargetId, name: "target-id", description: "The id of the target task logs to create or replace");
                    m.Option(c => ref c.CopyState, name: "copy-state", description: "Whether to copy the full state of the source record", defaultValue: result.CopyState);
                    m.Option(c => ref c.Order, name: "order", description: "The order of the created task", defaultValue: Env.JobPositionInPhase.ToNullable());
                    m.Option(c => ref c.Debug, name: "debug");

                    return result;
                },
                r => r.RunAsync()),

            CliModel.Bind<ReserveOperation>(
                new Command("reserve", "Reserve a slot for the specified task"),
                m =>
                {
                    var result = new ReserveOperation(m.Console)
                    {
                        TaskUrl = m.Option(c => ref c.TaskUrl, name: "taskUrl", required: true,
                            defaultValue: Env.TaskUri,
                            description: $"annotated build task uri (e.g. {TaskUriTemplate} )"),
                        AdoToken = m.Option(c => ref c.AdoToken, name: "token", description: "The access token (e.g. $(System.AccessToken) )", required: true),
                        JobCount = m.Option(c => ref c.JobCount, name: "jobCount", description: "The number of job slots available for reservation", required: true),
                    };

                    m.Option(c => ref c.AgentName, name: "agentName", description: "The name of the agent");
                    m.Option(c => ref c.PollSeconds, name: "pollSeconds", defaultValue: 5);
                    m.Option(c => ref c.CheckOnly, name: "checkOnly");
                    m.Option(c => ref c.Debug, name: "debug");

                    return result;
                },
                r => r.RunAsync())
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
            CliModel.Bind<UploadOperation>(
                new Command("upload"),
                m =>
                {
                    var result = new UploadOperation(m.Console, m.Token)
                    {
                        Source = m.Option(c => ref c.Source, name: "source", description: "The source file path", required: true),
                        TargetUri = m.Option(c => ref c.TargetUri, name: "target", description: "The target blob uri", required: true),
                    };

                    m.Option(c => ref c.Overwrite, name: "overwrite", defaultValue: false);

                    return result;
                },
                r => r.RunAsync()),

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