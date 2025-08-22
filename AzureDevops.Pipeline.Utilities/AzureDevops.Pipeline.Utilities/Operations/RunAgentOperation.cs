using System.Collections.ObjectModel;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO.Compression;
using System.Management.Automation;

namespace AzureDevops.Pipeline.Utilities;

public class RunAgentOperation(IConsole Console) : TaskOperationBase(Console)
{
    public string[]? AdditionalArgs;

    public required string WorkDirectory;

    public required string AgentDirectory;

    public required string AgentPoolName;

    public string? AgentName;

    public CleanMode Clean = CleanMode.None;

    public string? AgentPackageUrl;

    public string? AgentPackagePath;

    public Uri? OrganizationUrl;

    public enum CleanMode
    {
        None = 0,
        Work = 1 << 0,
        Agent = 1 << 1,
        All = Work | Agent
    }

    protected override async Task<int> RunCoreAsync()
    {
        if (Clean.HasFlag(CleanMode.Agent) && Directory.Exists(AgentDirectory)) Directory.Delete(AgentDirectory, true);
        if (Clean.HasFlag(CleanMode.Work) && Directory.Exists(WorkDirectory)) Directory.Delete(WorkDirectory, true);

        var envMap = new Dictionary<string, string?>()
        {
            ["AZP_URL"] = (OrganizationUrl ?? adoBuildUri.OrganizationUri).ToString(),
            ["AZP_TOKEN"] = AdoToken,
            ["AZP_AGENT_DIR"] = Path.GetFullPath(AgentDirectory),
            ["AZP_WORK"] = Path.GetFullPath(WorkDirectory),
            ["AZP_POOL"] = AgentPoolName,
            ["AZP_TASK_URL"] = TaskUrl,
            ["AZP_AGENT_NAME"] = AgentName,
            ["AZP_PACKAGE_URL"] = AgentPackageUrl,
            ["AZP_CUSTOM_PACKAGE_PATH"] = AgentPackagePath,
        };

        foreach (var (name, value) in envMap)
        {
            if (value != null)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }


        var args = CommandLineStringSplitter.Instance.Split($"-NoLogo -NoProfile -ExecutionPolicy Bypass -Command \"{Path.Combine(AppContext.BaseDirectory, "startup.ps1")}\"").ToArray();

        args = args.Concat(AdditionalArgs ?? Array.Empty<string>()).ToArray();

        Environment.SetEnvironmentVariable("PSModulePath", $"{AppContext.BaseDirectory};{Environment.GetEnvironmentVariable("PSModulePath")}");

        return Microsoft.PowerShell.ConsoleShell.Start("Running azputils powershell agent startup script.", helpText: null, args: args);
    }
}