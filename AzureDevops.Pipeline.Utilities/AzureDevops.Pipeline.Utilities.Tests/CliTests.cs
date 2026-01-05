using System.Collections.ObjectModel;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using System.Management.Automation;
using Azure.Storage.Blobs.Specialized;
using FluentAssertions;

namespace AzureDevops.Pipeline.Utilities.Tests;

public class CliTests
{
    [Fact]
    public async Task TestHelp()
    {
        await Program.RunAsync(Array.Empty<string>());

        await Program.RunAsync(["synchronize"]);

        await Program.RunAsync(["test", "--args", "hello", "--world"]);

    }

    public record InitRecordBase
    {
        protected virtual int InitHandler
        {
            get
            {
                return 0;
            }
            init
            {
            }
        }

        public InitRecordBase()
        {
            InitHandler = 0;
        }
    }

    [Fact]
    public async Task TestPowershell()
    {
        using (PowerShell PowerShellInst = PowerShell.Create())
        {
            string criteria = "system*";
            PowerShellInst.AddScript("Get-Service -DisplayName " + criteria);
            Collection<PSObject> PSOutput = PowerShellInst.Invoke();
            foreach (PSObject obj in PSOutput)
            {
                if (obj != null)
                {
                    Console.Write(obj.Properties["Status"].Value.ToString() + " - ");
                    Console.WriteLine(obj.Properties["DisplayName"].Value.ToString());
                }
            }
            Console.WriteLine("Done");
            Console.Read();
        }

        await Program.RunAsync(new("pwsh"));
    }

    [Fact]
    public void TestRemainingArgs()
    {
        var rootCommand = new RootCommand("Demo command");

        var command = new Command("pwsh");
        rootCommand.Add(command);

        var remainingArgs = new Argument<string[]>("remaining")
        {
            Arity = ArgumentArity.ZeroOrMore
        };

        command.AddArgument(remainingArgs);

        command.SetHandler((string[] remaining) =>
        {
            Console.WriteLine("Captured arguments after --:");
            Console.WriteLine(string.Join(" ", remaining));
        }, remainingArgs);

        var result = rootCommand.Parse("pwsh test -- first second third");

        result.Invoke();
    }
}