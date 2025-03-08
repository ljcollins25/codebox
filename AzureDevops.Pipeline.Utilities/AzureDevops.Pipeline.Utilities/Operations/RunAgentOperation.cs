using System.Collections.ObjectModel;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO.Compression;
using System.Management.Automation;

namespace AzureDevops.Pipeline.Utilities;

public class RunAgentOperation
{
    public required string AdoToken;

    public required string WorkDirectory;

    public required string AgentDirectory;

    public required Uri OrganizationUrl;

    public required string AgentPoolName;

    public string? AgentName;

    public bool Clean;

    public string? AgentPackageUrl;

    public string? TaskUrl;

    public async Task<int> RunAsync()
    {
        var envMap = new Dictionary<string, string?>()
        {
            ["AZP_URL"] = OrganizationUrl.ToString(),
            ["AZP_TOKEN"] = TaskOperationBase.FromDebugString(AdoToken),
            ["AZP_AGENT_DIR"] = AgentDirectory,
            ["AZP_WORK"] = WorkDirectory,
            ["AZP_POOL"] = AgentPoolName,
            ["AZP_TASK_URL"] = TaskUrl,
            ["AZP_AGENT_NAME"] = AgentName,
            ["AZP_AGENT_CLEAN"] = Clean ? "1" : "0",
            ["AZP_PACKAGE_URL"] = AgentPackageUrl,
        };

        foreach (var (name, value) in envMap)
        {
            if (value != null)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        Environment.SetEnvironmentVariable("PSModulePath", $"{AppContext.BaseDirectory};{Environment.GetEnvironmentVariable("PSModulePath")}");

        var args = CommandLineStringSplitter.Instance.Split($"-NoLogo -NoProfile -ExecutionPolicy Bypass -Command \"{Path.Combine(AppContext.BaseDirectory, "startup.ps1")}\"").ToArray();
        return Microsoft.PowerShell.ConsoleShell.Start("Running azputils powershell agent startup script.", helpText: null, args: args);
    }
}