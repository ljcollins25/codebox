using System;
using System.CommandLine;
using System.Diagnostics;

namespace AzureDevops.Pipeline.Utilities;

using static Helpers;

public class Program
{
    public static async Task<int> Main(params string[] args)
    {
        var precedingArgs = new List<string>();
        var remainingArgs = new List<string>();

        var list = precedingArgs;
        foreach (var arg in args)
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

        var rootCommand = new RootCommand
        {
            CliModel.Bind<RunOperation>(
                new Command("runagent", "Run command until it completes or build finishes. Also completes agent invocation task."),
                m =>
                {
                    var result = new RunOperation(m.Console, cts, agentRunner)
                    {
                        TaskUrl = m.Option(c => ref c.TaskUrl, name: "taskUrl", required: true,
                            defaultValue: Env.TaskUri,
                            description: $"annotated build task uri (e.g. {TaskUriTemplate} )"),
                        AdoToken = m.Option(c => ref c.AdoToken, name: "token", description: "The access token (e.g. $(System.AccessToken) )", required: true),
                    };

                    m.Option(c => ref c.PollSeconds, name: "pollSeconds");
                    m.Option(c => ref c.AgentTimeoutSeconds, name: "timeoutSeconds");
                    m.Option(c => ref c.Debug, name: "debug");

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
                        AdoToken = m.Option(c => ref c.AdoToken, name: "token", description: "The access token (e.g. $(System.AccessToken) )", required: true),
                        JobCount = m.Option(c => ref c.JobCount,
                            name: "jobCount",
                            defaultValue: Env.TotalJobsInPhase,
                            description: "The number of job slots available for reservation", required: true),
                    };

                    m.Option(c => ref c.PollSeconds, name: "pollSeconds", defaultValue: 5);
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

        return await rootCommand.InvokeAsync(precedingArgs.ToArray());
    }
}