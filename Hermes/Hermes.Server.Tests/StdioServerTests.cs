using System.Text.Json;
using Hermes.Core;
using Hermes.Verbs;
using Xunit;

namespace Hermes.Server.Tests;

/// <summary>
/// Tests for the StdioServer class.
/// </summary>
public class StdioServerTests
{
    private readonly StdioServer _server;

    public StdioServerTests()
    {
        var options = Program.SerializerOptions;
        var executor = new HermesVerbExecutor(options);
        var outputDir = Path.Combine(Path.GetTempPath(), "hermes-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(outputDir);
        VerbRegistration.RegisterAll(executor, outputDir);
        _server = new StdioServer(executor, options);
    }

    [Fact]
    public void ProcessRequest_HelpVerb_ReturnsVerbList()
    {
        var response = _server.ProcessRequest("""{"verb":"help","arguments":{}}""");
        var doc = JsonDocument.Parse(response);
        
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public void ProcessRequest_WithRequestId_ReturnsIdInResponse()
    {
        var response = _server.ProcessRequest("""{"id":"test-123","verb":"help","arguments":{}}""");
        var doc = JsonDocument.Parse(response);
        
        Assert.Equal("test-123", doc.RootElement.GetProperty("id").GetString());
        Assert.True(doc.RootElement.TryGetProperty("result", out var result));
        Assert.True(result.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public void ProcessRequest_YamlInput_Succeeds()
    {
        var yaml = @"verb: help
arguments: {}";
        var response = _server.ProcessRequest(yaml);
        var doc = JsonDocument.Parse(response);
        
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_ProcessesMultipleRequests()
    {
        var input = new StringReader("""
{"verb":"help","arguments":{}}
{"verb":"help","arguments":{"verb":"help"}}
""");
        var output = new StringWriter();
        
        await _server.RunAsync(input, output);
        
        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, responses.Length);
        
        foreach (var response in responses)
        {
            var doc = JsonDocument.Parse(response);
            Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
        }
    }

    [Fact]
    public async Task RunAsync_SkipsEmptyLines()
    {
        var input = new StringReader("""

{"verb":"help","arguments":{}}

""");
        var output = new StringWriter();
        
        await _server.RunAsync(input, output);
        
        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(responses);
    }
}
