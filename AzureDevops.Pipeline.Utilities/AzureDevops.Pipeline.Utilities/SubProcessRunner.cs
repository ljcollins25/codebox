using System.Diagnostics;

namespace AzureDevops.Pipeline.Utilities;

public class SubProcessRunner(string executable, IEnumerable<string> args, CancellationToken token)
{
    public async Task<int> RunAsync()
    {
        Console.WriteLine($"Executable: {executable}");
        Console.WriteLine($"Arguments: {string.Join(" ", args)}");
        int exitCode = -1;

        int maxRetryCount = 3;
        for (int i = 1; i <= maxRetryCount; i++)
        {
            ProcessStartInfo processStartInfo = new ProcessStartInfo(executable, args)
            {
                UseShellExecute = false,
            };

            processStartInfo.EnvironmentVariables.Remove("PSModulePath");

            Process process = new Process();
            process.StartInfo = processStartInfo;

            process.Start();
            using var r = token.Register(() =>
            {
                try
                {
                    process.Kill();
                }
                catch { }
            });

            await process.WaitForExitAsync();

            exitCode = process.ExitCode;
            if (exitCode == 0)
            {
                Console.WriteLine("SUCCESS: Process completed with code '{0}'", exitCode);
                return exitCode;
            }

            Thread.Sleep(TimeSpan.FromSeconds(5));
            Console.WriteLine("::warning::Process exited with code '{0}'." + ((i != maxRetryCount) ? " Retrying..." : " Reached max retry count. Failing."),
                exitCode);
        }

        return exitCode;
    }
}
