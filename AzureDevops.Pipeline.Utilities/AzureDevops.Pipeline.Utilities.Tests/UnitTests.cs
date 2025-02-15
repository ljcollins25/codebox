using System.CommandLine;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using FluentAssertions;

namespace AzureDevops.Pipeline.Utilities.Tests;

public class UnitTests
{
    public static async Task TestCommand(string args, int expectedValue = 0)
    {
        Console.SetError(Console.Out);
        Console.Error.WriteLine($"Running '{args}'");
        var result = await Program.RunAsync(new (CommandLineStringSplitter.Instance.Split(args).ToArray())
        {
            UseExceptionHandler = false
        });
        result.Should().Be(expectedValue);
    }

    [Fact]
    public async Task TestHelp()
    {
        await TestCommand("""download-log """);
        await TestCommand("info");



        await TestCommand("""copy-log --order 3 --parent-job-name "Shared job" --name "Shared task" """);

        await Program.Main();
        await Program.Main("test", "--args", "hello", "world");


        return;

        await Program.Main();

        await Program.Main("storage");

        await Program.Main("storage", "account");

        await Program.Main("storage", "account", "generate-sas");

        await Program.Main("storage", "blob", "generate-sas");

        await Program.Main("storage", "container", "generate-sas");

        await Program.Main("storage", "container", "generate-sas",
            "--account-name", TestSecrets.StorageAccountName,
            "--account-key", TestSecrets.StorageAccountKey,
            "--name", "enlistments",
            "--permissions", "rwdl",
            "--expiry", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        await Program.Main("storage", "blob", "generate-sas",
            "--account-name", TestSecrets.StorageAccountName,
            "--account-key", TestSecrets.StorageAccountKey,
            "--container-name", "enlistments",
            "--name", "ctpl.vhdx",
            "--permissions", "rwdl",
            "--expiry", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            );

        Console.WriteLine("Account sas");
        await Program.Main("storage", "account", "generate-sas",
            "--account-name", TestSecrets.StorageAccountName,
            "--account-key", TestSecrets.StorageAccountKey,
            "--permissions", "rwdl",
            "--resource-types", "sco",
            "--services", "b",
            "--expiry", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            );

    }

    [Fact]
    public async Task TestEnv()
    {
        var envLines = File.ReadAllLines(@"C:\bin\env.txt");
        foreach (var envLine in envLines)
        {
            var index = envLine.AsSpan().IndexOf("=");

            Environment.SetEnvironmentVariable(envLine.Substring(0, index), envLine.Substring(index + 1));
        }
        await TestCommand(""" synchronize --pollSeconds 2 --token "$(System.AccessToken)" --debug """);
    }

    [Fact]
    public async Task Test1()
    {
        var s = Helpers.Env.JobDisplayName;

        var command = Program.GetCommand();

        var pr = command.Parse("test --port 1 --port 2 --bool true --bool false");

        await pr.InvokeAsync();

        var parseResult = command.Parse($"synchronize --taskUrl \"{TestSecrets.TaskUrl}\" --unknownparam 1 2 3 --token \"{TestSecrets.AdoToken}\" --jobCount 3 --phaseId a333f115-f6a0-5054-279a-23236c10ac0a");

        await parseResult.InvokeAsync();


    }

    [Fact]
    public async Task TestOperation()
    {
        var taskUrl = TestSecrets.TaskUrl;

        var taskOperation = new TestTaskOperation(new SystemConsole())
        {
            AdoToken = TestSecrets.AdoToken,
            TaskUrl = taskUrl.Replace("$(taskId)", "78b71963-2023-5666-3648-28ff644d1619")
        };

        await taskOperation.InitilializeAsync();

        var records = await taskOperation.RefreshTimelineRecordsAsync();

        var downloadRecords = records.Where(r => r.Name?.Contains("(Download") == true).ToArray();

        var console = new SystemConsole();
        foreach (var record in downloadRecords)
        {
            var extract = new LogExtractOperation(console)
            {
                AdoToken = taskOperation.AdoToken,
                TaskUrl = taskUrl.Replace("$(taskId)", record.Id.ToString()),
                EndLine = -100,
                Patterns = new() { @"(ProxyInitialIndex.*\s+(?<ProxyInitialIndex>\d+),)|" }
            };

            await extract.InitilializeAsync();

            var values = await extract.GetValuesAsync();

            var jobRecord = await extract.GetRecordAsync(record.ParentId!.Value);

            int order = int.Parse(values["ProxyInitialIndex"]);

            var copy = new CopyLogOperation(console)
            {
                PhaseId = jobRecord.ParentId!.Value,
                TaskUrl = extract.TaskUrl,
                AdoToken = taskOperation.AdoToken,
                ParentJobName = "Downloads Summary",
                Name = $"Download ({order}) [{jobRecord.Name}]",
                Order = order,
            };

            await copy.RunAsync();
        }

        var extract1 = new LogExtractOperation(new SystemConsole())
        {
            AdoToken = taskOperation.AdoToken,
            TaskUrl = taskOperation.TaskUrl,
            EndLine = -100,
            Patterns = new() { @"ProxyInitialIndex.*\s+(?<ProxyInitialIndex>\d+)," }
        };

        await extract1.RunAsync();

        return;
    }

    public class TestTaskOperation(IConsole Console) : TaskOperationBase(Console)
    {
        protected override async Task<int> RunCoreAsync()
        {
            var timeline1 = await client.GetBuildTimelineAsync(build.Project.Id, build.Id);

            var record = new TimelineRecord()
            {
                Id = taskInfo.JobId,
                Name = "Job name update",
                RecordType = "Job"
            };

            var readRecord = await UpdateTimelineRecordAsync(record);

            var newRecord = new TimelineRecord()
            {
                //Id = Guid.Parse("4a1141e7-08fb-44c7-9a11-d2e8ae99914f"),
                Id = Guid.NewGuid(),
                Name = $"Test record ({DateTime.Now:yyyyMMdd-hhmmss})",
                //ParentId = readRecord.Id,
                //ParentId = Guid.Parse("002b9fe5-afbf-5aed-69fa-2f4a880aae97"),
                ParentId = Guid.Parse("a333f115-f6a0-5054-279a-23236c10ac0a"),
                RecordType = "Task",
                Log = readRecord.Log
            };

            //

            var readNewRecord = await UpdateTimelineRecordAsync(newRecord);

            var timeline = await client.GetBuildTimelineAsync(build.Project.Id, build.Id);

            return 0;
        }

        private record ParticipantEntry(string AgentName, DateTime TimeStamp);
    }
}