using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Hermes.Server.Tests;

/// <summary>
/// Integration tests for Hermes HTTP Server.
/// </summary>
public class HttpServerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public HttpServerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task HealthCheck_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health");
        
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("healthy", content);
    }

    [Fact]
    public async Task ListVerbs_ReturnsVerbList()
    {
        var response = await _client.GetAsync("/verbs");
        
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        
        Assert.True(doc.RootElement.TryGetProperty("verbs", out var verbs));
        Assert.True(verbs.GetArrayLength() > 0);
        
        // Check that help verb is present
        var verbNames = verbs.EnumerateArray()
            .Select(v => v.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("help", verbNames);
    }

    [Fact]
    public async Task GetSchema_ReturnsSchemaForVerb()
    {
        var response = await _client.GetAsync("/schema/help");
        
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        
        Assert.Equal("help", doc.RootElement.GetProperty("verb").GetString());
        Assert.True(doc.RootElement.TryGetProperty("description", out _));
        Assert.True(doc.RootElement.TryGetProperty("argumentsSchema", out _));
        Assert.True(doc.RootElement.TryGetProperty("resultSchema", out _));
    }

    [Fact]
    public async Task GetSchema_UnknownVerb_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/schema/unknown-verb");
        
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Execute_HelpVerb_ReturnsVerbList()
    {
        var request = new StringContent(
            """{"verb":"help","arguments":{}}""",
            Encoding.UTF8,
            "application/json");
        
        var response = await _client.PostAsync("/execute", request);
        
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("verbs", out var verbs));
        Assert.True(verbs.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Execute_HelpVerbWithName_ReturnsVerbHelp()
    {
        var request = new StringContent(
            """{"verb":"help","arguments":{"verb":"help"}}""",
            Encoding.UTF8,
            "application/json");
        
        var response = await _client.PostAsync("/execute", request);
        
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("verbs", out var verbs));
        Assert.True(verbs.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Execute_YamlInput_ReturnsJsonResult()
    {
        var yaml = @"verb: help
arguments: {}";
        var request = new StringContent(yaml, Encoding.UTF8, "text/plain");
        
        var response = await _client.PostAsync("/execute", request);
        
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task Execute_UnknownVerb_ReturnsError()
    {
        var request = new StringContent(
            """{"verb":"unknown-verb","arguments":{}}""",
            Encoding.UTF8,
            "application/json");
        
        var response = await _client.PostAsync("/execute", request);
        
        // Should still return 200 with error in body (VerbResult pattern)
        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        
        Assert.False(doc.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("errorMessage", out _));
    }

    [Fact]
    public async Task Execute_EmptyBody_ReturnsBadRequest()
    {
        var request = new StringContent("", Encoding.UTF8, "application/json");
        
        var response = await _client.PostAsync("/execute", request);
        
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Execute_InvalidJson_ReturnsBadRequest()
    {
        var request = new StringContent("not valid json or yaml {{{{", Encoding.UTF8, "application/json");
        
        var response = await _client.PostAsync("/execute", request);
        
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
