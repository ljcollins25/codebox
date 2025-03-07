using System.CommandLine;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class RunOperation(IConsole Console, CancellationTokenSource agentCancellation, SubProcessRunner? agentRunner = null) : TaskOperationBase(Console)
{
    public double AgentTimeoutSeconds = 30;

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

        var records = await taskClient.UpdateTimelineRecordsAsync(
            scopeIdentifier: build.Project.Id,
            planType: taskInfo.HubName,
            planId: taskInfo.PlanId,
            timelineId: taskInfo.TimelineId,
            new[]
            {
                new TimelineRecord()
                {
                    Id = taskInfo.TaskId,
                    Variables =
                    {
                        ["TaskId"] = taskInfo.TaskId.ToString()
                    }
                }
            });

        var record = records[0];

        async Task setTaskResult(TaskResult result)
        {
            if (record.Result == null)
            {
                Console.WriteLine($"Setting result to {result}");
                await RaisePlanEventAsync(GetTaskCompletedEvent(result));
            }
            else
            {
                Console.WriteLine($"Skipping due to exit attempted result: {result}, actual result: {record.Result}");
            }
        }

        try
        {
            await setTaskResult(TaskResult.Succeeded);

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
                await setTaskResult(TaskResult.Canceled);
            }
        }
    }
}