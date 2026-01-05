using System.Text.Json;
using System.Text.Json.Serialization;
using Hermes.Core;
using Hermes.Verbs;

namespace Hermes.Server;

/// <summary>
/// Long-running Hermes server that processes Verb requests over stdin/stdout.
/// Each line is a complete YAML or JSON request, and responses are written as complete JSON lines.
/// </summary>
public class Program
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        var workspaceRoot = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
        var outputDirectory = Path.Combine(workspaceRoot, ".hermes", "output");

        // Ensure output directory exists
        Directory.CreateDirectory(outputDirectory);

        var executor = new HermesVerbExecutor(SerializerOptions);
        VerbRegistration.RegisterAll(executor, outputDirectory);

        var server = new HermesServer(executor, SerializerOptions);
        
        Console.Error.WriteLine($"Hermes Server started. Workspace: {workspaceRoot}");
        Console.Error.WriteLine("Ready to receive requests on stdin...");

        await server.RunAsync(Console.In, Console.Out);

        return 0;
    }
}

/// <summary>
/// Hermes server that processes requests from a TextReader and writes responses to a TextWriter.
/// </summary>
public sealed class HermesServer
{
    private readonly HermesVerbExecutor _executor;
    private readonly JsonSerializerOptions _serializerOptions;

    public HermesServer(HermesVerbExecutor executor, JsonSerializerOptions serializerOptions)
    {
        _executor = executor;
        _serializerOptions = serializerOptions;
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken);
            
            if (line == null)
            {
                // EOF
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = ProcessRequest(line);
            await output.WriteLineAsync(response);
            await output.FlushAsync(cancellationToken);
        }
    }

    private string ProcessRequest(string request)
    {
        try
        {
            // Parse the request to get the request ID if present
            var requestDoc = JsonDocument.Parse(YamlToJsonConverter.NormalizeToJson(request));
            string? requestId = null;
            
            if (requestDoc.RootElement.TryGetProperty("id", out var idElement))
            {
                requestId = idElement.GetString();
            }

            // Execute handles YAML/JSON normalization internally
            var result = _executor.Execute(request);

            // If there was a request ID, wrap the response
            if (requestId != null)
            {
                return JsonSerializer.Serialize(new ServerResponse
                {
                    Id = requestId,
                    Result = JsonDocument.Parse(result).RootElement
                }, _serializerOptions);
            }

            return result;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                succeeded = false,
                errorMessage = ex.Message
            }, _serializerOptions);
        }
    }
}

/// <summary>
/// Server response envelope with optional request ID correlation.
/// </summary>
public sealed class ServerResponse
{
    public string? Id { get; init; }
    public JsonElement Result { get; init; }
}
