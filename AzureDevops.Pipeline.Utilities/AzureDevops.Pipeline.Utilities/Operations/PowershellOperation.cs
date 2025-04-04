using System.Collections.ObjectModel;
using System.CommandLine;
using System.IO.Compression;
using System.Management.Automation;
using static AzureDevops.Pipeline.Utilities.Program;

namespace AzureDevops.Pipeline.Utilities;

public class PowershellOperation()
{
    public required string[] CommandArgs;

    public string[]? AdditionalArgs;

    public async Task<int> RunAsync()
    {
        Environment.SetEnvironmentVariable("PSModulePath", $"{AppContext.BaseDirectory};{Environment.GetEnvironmentVariable("PSModulePath")}");

        var args = CommandArgs.Concat(AdditionalArgs ?? Array.Empty<string>()).ToArray();

        return Microsoft.PowerShell.ConsoleShell.Start("Running azputils powershell", helpText: null, args: args);
    }
}