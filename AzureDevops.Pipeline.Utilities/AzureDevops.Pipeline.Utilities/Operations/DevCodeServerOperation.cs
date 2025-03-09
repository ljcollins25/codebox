using System.Collections.ObjectModel;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO.Compression;
using System.Management.Automation;

namespace AzureDevops.Pipeline.Utilities;

public class DevCodeServerOperation
{
    public required string Workspace;

    public required string Name;

    public LoginProvider Provider = LoginProvider.microsoft;

    public enum LoginProvider
    {
        microsoft,
        github
    }

    public async Task<int> RunAsync()
    {
        var envMap = new Dictionary<string, string?>()
        {
            ["AZPUTILS_DCS_WORKSPACE"] = Workspace,
            ["AZPUTILS_DCS_NAME"] = Name,
            ["AZPUTILS_DCS_PROVIDER"] = Provider.ToString()
        };

        foreach (var (name, value) in envMap)
        {
            if (value != null)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        Environment.SetEnvironmentVariable("PSModulePath", $"{AppContext.BaseDirectory};{Environment.GetEnvironmentVariable("PSModulePath")}");

        var args = CommandLineStringSplitter.Instance.Split($"-NoLogo -NoProfile -ExecutionPolicy Bypass -Command \"{Path.Combine(AppContext.BaseDirectory, "dev-code-server.ps1")}\"").ToArray();
        return Microsoft.PowerShell.ConsoleShell.Start("Running azputils powershell agent startup script.", helpText: null, args: args);
    }
}