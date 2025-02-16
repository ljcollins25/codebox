using System.CommandLine;

namespace AzureDevops.Pipeline.Utilities;


public class UpdateRecordOperation(IConsole Console) : TaskOperationBase(Console)
{
    public Guid? Id;

    public string? Name;

    public int? PercentComplete;

    public Guid? ParentId;

    public RecordTypes? RecordType;

    public TaskResult? Result;

    protected override async Task<int> RunCoreAsync()
    {
        var record = await UpdateTimelineRecordAsync(new()
        {
            Id = Id ?? taskInfo.TaskId,
            Name = Name,
            ParentId = ParentId,
            RecordType = RecordType?.ToString(),
            PercentComplete = PercentComplete
        });

        Console.WriteLine($"Updated {record.RecordType} record {record.Id}: Name='{record.Name}'");

        return 0;
    }
}
