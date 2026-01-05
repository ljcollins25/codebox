using System.Text.Json;
using System.Text.Json.Serialization;
using Hermes.Core;
using Hermes.Verbs;
using Microsoft.AspNetCore.Mvc;

namespace Hermes.Server;

/// <summary>
/// Hermes server with HTTP and stdio modes.
/// HTTP mode: ASP.NET Core server with /execute endpoint.
/// stdio mode: Line-delimited JSON protocol for MCP integration.
/// </summary>
public class Program
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        // Check for stdio mode
        if (args.Contains("--stdio"))
        {
            return await RunStdioMode(args);
        }

        // Default to HTTP server mode
        return await RunHttpMode(args);
    }

    private static async Task<int> RunHttpMode(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Configure JSON serialization
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        // Add services
        builder.Services.AddSingleton(SerializerOptions);
        builder.Services.AddSingleton<HermesVerbExecutor>(sp =>
        {
            var executor = new HermesVerbExecutor(SerializerOptions);
            var outputDir = Path.Combine(builder.Environment.ContentRootPath, ".hermes", "output");
            Directory.CreateDirectory(outputDir);
            VerbRegistration.RegisterAll(executor, outputDir);
            return executor;
        });

        // Add OpenAPI/Swagger
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // Configure middleware
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        // Map endpoints
        app.MapPost("/execute", ExecuteEndpoint)
            .WithName("ExecuteVerb")
            .WithDescription("Execute a Hermes Verb");

        app.MapGet("/verbs", ListVerbsEndpoint)
            .WithName("ListVerbs")
            .WithDescription("List all available Verbs");

        app.MapGet("/schema/{verb}", GetSchemaEndpoint)
            .WithName("GetVerbSchema")
            .WithDescription("Get schema for a specific Verb");

        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
            .WithName("HealthCheck")
            .WithDescription("Health check endpoint");

        Console.WriteLine($"Hermes HTTP Server starting on {builder.Configuration["Urls"] ?? "http://localhost:5000"}");

        await app.RunAsync();
        return 0;
    }

    private static async Task<IResult> ExecuteEndpoint(
        HttpRequest request,
        HermesVerbExecutor executor)
    {
        try
        {
            using var reader = new StreamReader(request.Body);
            var input = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(input))
            {
                return Results.BadRequest(new { succeeded = false, errorMessage = "Request body is required" });
            }

            var result = executor.Execute(input);
            var jsonDoc = JsonDocument.Parse(result);
            return Results.Ok(jsonDoc.RootElement);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { succeeded = false, errorMessage = ex.Message });
        }
    }

    private static IResult ListVerbsEndpoint(HermesVerbExecutor executor, string? verb = null)
    {
        var registrations = executor.GetRegistrations();
        
        if (!string.IsNullOrEmpty(verb))
        {
            registrations = registrations.Where(r => r.Name.Equals(verb, StringComparison.OrdinalIgnoreCase));
        }

        var verbs = registrations
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                name = r.Name,
                description = r.Description ?? "No description available."
            });

        return Results.Ok(new { verbs });
    }

    private static IResult GetSchemaEndpoint(string verb, HermesVerbExecutor executor)
    {
        var registration = executor.GetRegistration(verb);

        if (registration == null)
        {
            return Results.NotFound(new { succeeded = false, errorMessage = $"Verb '{verb}' not found" });
        }

        return Results.Ok(new
        {
            verb = registration.Name,
            description = registration.Description,
            argumentsSchema = JsonSchemaBuilder.GetSchema(registration.ArgumentType, SerializerOptions),
            resultSchema = JsonSchemaBuilder.GetSchema(registration.ResultType, SerializerOptions)
        });
    }

    private static async Task<int> RunStdioMode(string[] args)
    {
        var workspaceRoot = args.FirstOrDefault(a => !a.StartsWith("-")) ?? Environment.CurrentDirectory;
        var outputDirectory = Path.Combine(workspaceRoot, ".hermes", "output");

        Directory.CreateDirectory(outputDirectory);

        var executor = new HermesVerbExecutor(SerializerOptions);
        VerbRegistration.RegisterAll(executor, outputDirectory);

        var server = new StdioServer(executor, SerializerOptions);

        Console.Error.WriteLine($"Hermes Server (stdio mode) started. Workspace: {workspaceRoot}");
        Console.Error.WriteLine("Ready to receive requests on stdin...");

        await server.RunAsync(Console.In, Console.Out);

        return 0;
    }
}

/// <summary>
/// Hermes server that processes requests from a TextReader and writes responses to a TextWriter.
/// Used for stdio mode and MCP protocol.
/// </summary>
public sealed class StdioServer
{
    private readonly HermesVerbExecutor _executor;
    private readonly JsonSerializerOptions _serializerOptions;

    public StdioServer(HermesVerbExecutor executor, JsonSerializerOptions serializerOptions)
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

    public string ProcessRequest(string request)
    {
        try
        {
            var requestDoc = JsonDocument.Parse(YamlToJsonConverter.NormalizeToJson(request));
            string? requestId = null;

            if (requestDoc.RootElement.TryGetProperty("id", out var idElement))
            {
                requestId = idElement.GetString();
            }

            var result = _executor.Execute(request);

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
