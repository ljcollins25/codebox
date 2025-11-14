using System;
using System.Collections.Generic;
using System.CommandLine;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class VariableGroupOperation(IConsole Console) : TaskOperationBase(Console)
{
    public bool Load;

    public required string FilePath;

    public required string VariableGroupName;

    public string? VariableGroupProject = null;

    public bool MarkSecret = false;

    protected override async Task<int> RunCoreAsync()
    {
        VariableGroupProject ??= this.adoBuildUri.Project;

        var variableGroup = await this.agentClient.GetVariableGroupsAsync(
            VariableGroupProject,
            VariableGroupName,
            Load ? VariableGroupActionFilter.Use : VariableGroupActionFilter.Manage,
            top: 2)
            .ThenAsync(l => l.Single());

        if (Load)
        {
            //agentClient.UpdateVariableGroupAsync(variableGroup.Id, )
            //variableGroup.Variables[]
        }
        
        return 0;
    }
}
