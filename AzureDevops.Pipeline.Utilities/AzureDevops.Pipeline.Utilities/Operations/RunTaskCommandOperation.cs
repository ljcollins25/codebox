using System.CommandLine;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class RunTaskCommandOperation(IConsole Console, CancellationTokenSource agentCancellation, SubProcessRunner? agentRunner = null) : TaskOperationBase(Console)
{
    public double AgentTimeoutSeconds = 30;

    public int RetryCount = 3;

    public const string IsMarkedKey = "Marked";

    protected override async Task<int> RunCoreAsync()
    {
        Console.WriteLine($"Starting agent.");
        var runTask = agentRunner?.RunAsync();
        Console.WriteLine($"Started agent.");

        runTask?.ContinueWith(t =>
        {
            agentCancellation.Cancel();
        });

        await RunHelperAsync(agentCancellation);

        runTask ??= Task.FromResult(0);
        return await runTask;
    }

    private async Task RunHelperAsync(CancellationTokenSource agentCancellation)
    {
        Console.WriteLine($"Setting TaskId variable to {taskInfo.TaskId}.");

        var record = await UpdateTimelineRecordAsync(
            new()
            {
                Id = taskInfo.TaskId,
                Variables =
                    {
                        ["TaskId"] = taskInfo.TaskId.ToString()
                    }
            });

        try
        {
            await SetTaskResult(TaskResult.Succeeded, record, Console);

            while (!IsCompleted(build) && !agentCancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(PollSeconds));

                build = await client.GetBuildAsync(adoBuildUri.Project, adoBuildUri.BuildId);
            }

            if (!IsCompleted(build) && !(await GetBuildProperties()).ContainsKey(taskInfo.AllJobsReservedKey()))
            {
                AppendLinesToEnvFile(FileEnvVar.GITHUB_OUTPUT,
                    $"{OutputNames.hasMoreJobs}=true");
            }

            agentCancellation.CancelAfter(TimeSpan.FromSeconds(AgentTimeoutSeconds));
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException || ex is TaskCanceledException)
            {
                await SetTaskResult(TaskResult.Canceled, record, Console);
            }
        }
    }
}