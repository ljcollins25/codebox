using System.Collections.ObjectModel;
using System.CommandLine;
using System.IO.Compression;
using System.Management.Automation;

namespace AzureDevops.Pipeline.Utilities;

public class PowershellOperation()
{
    public required string[] Args;

    public async Task<int> RunAsync()
    {
        Environment.SetEnvironmentVariable("PSModulePath", $"{AppContext.BaseDirectory};{Environment.GetEnvironmentVariable("PSModulePath")}");

        return Microsoft.PowerShell.ConsoleShell.Start("Running azputils powershell", helpText: null, args: Args);
    }
}