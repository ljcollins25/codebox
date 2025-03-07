using System.CommandLine;
using System.Linq;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;


public class UpdateRecordOperation(IConsole Console) : TaskOperationBase(Console)
{
    public Guid? Id;

    public string? Name;

    public int? PercentComplete;

    public Guid? ParentId;

    public RecordTypes? RecordType;

    public TaskResult? Result;

    public List<string> Variables = new();

    public List<string> SecretVariables = new();

    public string? VariableInputPrefix;

    public string? VariableOutputPrefix;

    protected override async Task<int> RunCoreAsync()
    {
        Variables.AddRange(SecretVariables);
        VariableOutputPrefix ??= string.Empty;

        var secrets = SecretVariables.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (VariableInputPrefix.IsNonEmpty())
        {
            Variables.AddRange(Helpers.GetEnvironmentVariables()
                .Select(e => e.Key)
                .Where(k => k.StartsWith(VariableInputPrefix, StringComparison.OrdinalIgnoreCase)));
        }

        var record = await UpdateTimelineRecordAsync(new()
        {
            Id = Id ?? taskInfo.TaskId,
            Name = Name,
            ParentId = ParentId,
            RecordType = RecordType?.ToString(),
            PercentComplete = PercentComplete,
            Variables =
            {
                Variables
                    .Select(name =>
                        (name, value: Environment.GetEnvironmentVariable(VariableInputPrefix + name).AsNonEmptyOrNull()
                        ?? Helpers.GetEnvironmentVariable(name)))
                    .Where(e => e.value.IsNonEmpty())
                    .ToDictionary(e => VariableOutputPrefix + e.name, e => new VariableValue(e.value, isSecret: secrets.Contains(e.name)))
            }
        });

        Console.WriteLine($"Updated {record.RecordType} record {record.Id}: Name='{record.Name}'");

        return 0;
    }
}
