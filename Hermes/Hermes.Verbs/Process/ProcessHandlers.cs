using SysDiag = global::System.Diagnostics;

namespace Hermes.Verbs.Process;

/// <summary>
/// Unified process Verb handlers. All proc.* operations are implemented here.
/// </summary>
public static class ProcessHandlers
{
    /// <summary>
    /// proc.run - Execute a process and capture output.
    /// </summary>
    public static ProcRunResult Run(ProcRunArgs args, string outputDirectory)
    {
        // Ensure output directory exists
        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var stdoutPath = Path.Combine(outputDirectory, $"proc_{timestamp}_stdout.txt");
        var stderrPath = Path.Combine(outputDirectory, $"proc_{timestamp}_stderr.txt");

        try
        {
            var startInfo = new SysDiag.ProcessStartInfo
            {
                FileName = args.Executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args.Arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new SysDiag.Process();
            process.StartInfo = startInfo;

            var stdout = new List<string>();
            var stderr = new List<string>();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    stdout.Add(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    stderr.Add(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            File.WriteAllLines(stdoutPath, stdout);
            File.WriteAllLines(stderrPath, stderr);

            return new ProcRunResult
            {
                ExitCode = process.ExitCode,
                StdoutPath = stdoutPath,
                StderrPath = stderrPath
            };
        }
        catch (Exception ex)
        {
            // Write error to stderr file
            File.WriteAllText(stderrPath, ex.ToString());
            File.WriteAllText(stdoutPath, string.Empty);

            return new ProcRunResult
            {
                ExitCode = -1,
                StdoutPath = stdoutPath,
                StderrPath = stderrPath,
                Succeeded = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
