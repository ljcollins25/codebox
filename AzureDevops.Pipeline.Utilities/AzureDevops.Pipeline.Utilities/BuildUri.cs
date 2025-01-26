using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Web;

namespace AzureDevops.Pipeline.Utilities;

public record BuildUri(Uri OrganizationUri, string Project, NameValueCollection Parameters)
{
    public static JsonSerializerOptions SerializerOptions = new JsonSerializerOptions()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true
    };

    public int BuildId { get; set; }
    public int DefinitionId { get; set; }
    public string? SourceBranch { get; set; }
    public string? PersonalAccessToken { get; set; }

    public string GetBuildWebUrl()
    {
        return $"{OrganizationUri}{Project}/_build/results?buildId={BuildId}";
    }

    public string GetDefinitionWebUrl()
    {
        return $"{OrganizationUri}{Project}/_build?definitionId={DefinitionId}";
    }

    public string GetGitUrl(string repoName)
    {
        return $"{OrganizationUri}{Project}/_git/{repoName}";
    }

    public BuildUri WithBuildId(int? buildId)
    {
        return this with { BuildId = buildId ?? -1 };
    }

    public static BuildUri ParseBuildUri(string buildUrl)
    {
        var uri = new UriBuilder(buildUrl);
        var parameters = HttpUtility.ParseQueryString(uri.Query);
        var buildId = parameters["buildId"];
        var definitionId = parameters["definitionId"];
        var pat = !string.IsNullOrEmpty(uri.UserName) ? uri.UserName : null;
        if (string.IsNullOrEmpty("definitionId"))
        {
            definitionId = parameters["pipelineId"] ?? definitionId;
        }

        string project;
        if (uri.Host.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var projectStart = uri.Path.IndexOf('/', 1) + 1;
            var projectEnd = uri.Path.IndexOf('/', projectStart);
            projectEnd = projectEnd > 0 ? projectEnd : uri.Path.Length;
            project = uri.Path[projectStart..projectEnd];
            uri.Path = uri.Path.Substring(0, projectStart);
        }
        else
        {
            project = uri.Path[1..uri.Path.IndexOf('/', 1)];
            uri.Path = null;
        }

        uri.UserName = null;
        uri.Password = null;

        uri.Query = null;
        return new BuildUri(uri.Uri, project, parameters)
        {
            BuildId = ParseId(buildId),
            DefinitionId = ParseId(definitionId),
            PersonalAccessToken = pat
        };
    }

    public T DeserializeFromParameters<T>()
    {
        var obj = new JsonObject();
        foreach (string key in Parameters.Keys)
        {
            obj[key] = Parameters[key];
        }

        return obj.Deserialize<T>(SerializerOptions)!;
    }

    public static int ParseId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return -1;

        return int.Parse(id);
    }
}
