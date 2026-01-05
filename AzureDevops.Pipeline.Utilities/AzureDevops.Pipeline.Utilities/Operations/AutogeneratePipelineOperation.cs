using System.CommandLine;
using System.Management.Automation.Runspaces;
using Microsoft.Azure.Pipelines.WebApi;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace AzureDevops.Pipeline.Utilities;

public class AutogeneratePipelineOperation(IConsole Console) : TaskOperationBase(Console)
{
    public string? ProjectName { get; private set; }

    public required string RepositoryName;
    public string RepositoryNameOnly { get => field ?? RepositoryName; set; }

    public required string RepoDirectory;

    public string PipelinesRelativeFolder = ".azdev";
    private PipelinesHttpClient pclient;
    private GitHttpClient gitClient;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        pclient = connection.GetClient<PipelinesHttpClient>();
        gitClient = connection.GetClient<GitHttpClient>();
        RepositoryName = NormalizePath(RepositoryName);
        var parts = RepositoryName.Split('/');
        if (parts.Length == 2)
        {
            ProjectName = parts[0];
            RepositoryNameOnly = parts[1];
        }
    }

    protected override async Task<int> RunCoreAsync()
    {
        var existingPipelines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var pipelines = await pclient.ListPipelinesAsync(ProjectName ?? adoBuildUri.Project);
        foreach (var pipeline in pipelines)
        {
            if (pipeline.Configuration is not YamlConfiguration config) continue;

            var path = NormalizePath($"{pipeline.Folder}/{pipeline.Name}");

            existingPipelines.Add(path);
        }

        PipelinesRelativeFolder = NormalizePath(PipelinesRelativeFolder);

        var files = Directory.GetFiles(Path.Combine(RepoDirectory, PipelinesRelativeFolder), "*.yml", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);

            var qualifiedRepoName = NormalizePath(Path.Combine(ProjectName ?? string.Empty, RepositoryName));
            var path = NormalizePath($"{qualifiedRepoName}/{fileName}");

            var args = new CreatePipelineParameters()
            {
                Name = $"{fileName}",
                Folder = $"{qualifiedRepoName}",
                Configuration = new CreateYamlPipelineConfigurationParameters()
                {
                    Path = $"{PipelinesRelativeFolder}/{fileName}",
                    Repository = new CreateAzureReposGitRepositoryParameters()
                    {
                        Name = qualifiedRepoName,
                    }
                }
            };

            //client.UpdateDefinitionAsync()

            //pclient.CreatePipelineAsync(new CreatePipelineParameters()
            //{
            //    Folder = $"{ProjectName}/{RepositoryName}",
                
            //})
        }

        return 0;
    }

    public async Task CreatePipelineAsync(string fileName)
    {
        var path = NormalizePath($"{RepositoryName}/{fileName}");

        var repo = await gitClient.GetRepositoryAsync(ProjectName ?? adoBuildUri.Project, RepositoryNameOnly);

        var args = new CreatePipelineParameters()
        {
            Name = $"{fileName}",
            Folder = RepositoryName,
            Configuration = new CreateYamlPipelineConfigurationParameters()
            {
                Path = $"{PipelinesRelativeFolder}/{fileName}",
                Repository = new CreateAzureReposGitRepositoryParameters()
                {
                    Id  = repo.Id,
                    Name = RepositoryName,
                }
            }
        };

        var pipeline = await pclient.CreatePipelineAsync(args, adoBuildUri.Project);
    }

    public static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim('/').Replace("//", "/");
    }
}
