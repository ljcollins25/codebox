using System.CommandLine;

namespace AzureDevops.Pipeline.Utilities;


public class UpdateRecordOperation(IConsole Console) : TaskOperationBase(Console)
{
    public required Guid Id;

    public string? Name;

    public int? PercentComplete;

    public Guid? ParentId;

    public RecordTypes? RecordType;

    protected override async Task<int> RunCoreAsync()
    {
        await UpdateTimelineRecordAsync(new()
        {
            Id = Id,
            Name = Name,
            ParentId = ParentId,
            RecordType = RecordType?.ToString(),
            PercentComplete = PercentComplete
        });

        return 0;
    }
}
