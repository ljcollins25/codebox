using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Text;
using System.Text.Json;
using Azure.Storage;
using Azure.Storage.Sas;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public record SasCommonArguments
{
    public required string Permissions;

    public required DateTime Expiry;

    public required string AccountName;

    public required string AccountKey;

    public string? Output;
}

public abstract class SasArgumentsBase(IConsole console)
{
    public required SasCommonArguments CommonArguments;

    protected abstract SasQueryParameters GetSasCore();

    public async Task<int> RunAsync()
    {
        console.WriteLine(GetSas());
        return 0;
    }

    public string GetSas()
    {
        var parameters = GetSasCore();
        var query = parameters.ToString();

        query = query.TrimStart('?');

        return query;
    }

    protected static T ParseParam<T>(string value, string paramKey, Func<SasQueryParameters, T?> getValue)
        where T : struct
    {
        var parser = new SasQueryParamsParser(new Dictionary<string, string>()
        {
            { paramKey, value }
        });

        return getValue(parser) ?? default(T);
    }

    private class SasQueryParamsParser : SasQueryParameters
    {
        public SasQueryParamsParser(IDictionary<string, string> values) : base(values)
        {
        }
    }
}

public class BlobOrContainerArguments(IConsole console) : SasArgumentsBase(console)
{
    public required string ContainerName;

    public string? BlobName;

    protected override SasQueryParameters GetSasCore()
    {
        var builder = new BlobSasBuilder()
        {
            ExpiresOn = CommonArguments.Expiry,
            BlobContainerName = ContainerName,
            BlobName = BlobName,
        };

        builder.SetPermissions(CommonArguments.Permissions);

        return builder.ToSasQueryParameters(new StorageSharedKeyCredential(CommonArguments.AccountName, CommonArguments.AccountKey), out _);

    }
}

public class AccountSasArguments(IConsole console) : SasArgumentsBase(console)
{
    public required string Services;

    public required string ResourceTypes;

    protected override SasQueryParameters GetSasCore()
    {
        var builder = new AccountSasBuilder()
        {
            ExpiresOn = CommonArguments.Expiry,
            Services = ParseParam(Services, "ss", p => p.Services),
            ResourceTypes = ParseParam(ResourceTypes, "srt", p => p.ResourceTypes),
        };

        builder.SetPermissions(CommonArguments.Permissions);

        return builder.ToSasQueryParameters(new StorageSharedKeyCredential(CommonArguments.AccountName, CommonArguments.AccountKey), out _);
    }
}
