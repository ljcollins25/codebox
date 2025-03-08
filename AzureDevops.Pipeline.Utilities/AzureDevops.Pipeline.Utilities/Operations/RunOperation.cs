using System.CommandLine;
using System.IO.Compression;

namespace AzureDevops.Pipeline.Utilities;

public class RunOperation(SubProcessRunner? subProcessRunner)
{
    public int RetryCount = 1;

    public Task<int> RunAsync()
    {
        return subProcessRunner?.RunAsync(retryCount: RetryCount) ?? Task.FromResult(0);
    }
}