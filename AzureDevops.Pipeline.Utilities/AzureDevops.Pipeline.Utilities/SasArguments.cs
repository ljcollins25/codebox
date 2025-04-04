using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Text;
using System.Text.Json;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public record SasCommonArguments
{
    public required string Permissions;

    public required string ExpiryValue;

    public DateTimeOffset Expiry
    {
        get
        {
            if (DateTime.TryParse(ExpiryValue, out var d)) return d.ToUniversalTime();
            if (DateTimeOffset.TryParse(ExpiryValue, out var dto)) return dto;
            if (TimeSpan.TryParse(ExpiryValue, out var ts)) return DateTimeOffset.UtcNow + ts;
            if (TimeSpanSetting.TryParseReadableTimeSpan(ExpiryValue, out ts)) return DateTimeOffset.UtcNow + ts;

            throw new FormatException($"Unable to parse Expiry '{ExpiryValue}' as date or TimeSpan");
        }
    }

    public required string AccountName;

    public required string AccountKey;

    public bool EmitFullUri;

    public string? Output;

}

public abstract class SasArgumentsBase(IConsole console)
{
    public required SasCommonArguments CommonArguments;

    protected BlobUriBuilder UriBuilder = null!;

    protected abstract SasQueryParameters GetSasCore();

    public async Task<int> RunAsync()
    {
        console.WriteLine(Globals.GeneratedSas = GetSas());
        return 0;
    }

    public string GetSas()
    {
        UriBuilder = new BlobUriBuilder(new Uri($"https://{CommonArguments.AccountName}.blob.core.windows.net/"));
        var parameters = GetSasCore();

        var query = parameters.ToString();

        query = query.TrimStart('?');
        if (CommonArguments.EmitFullUri)
        {
            UriBuilder.Query = query;
            return UriBuilder.ToString();
        }

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
        UriBuilder.BlobContainerName = ContainerName;
        UriBuilder.BlobName = BlobName;

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
