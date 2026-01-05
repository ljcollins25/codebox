using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Hermes.Core;
using Hermes.Verbs;
using Hermes.Verbs.Help;
using Xunit;

namespace Hermes.Tests;

public class HelpVerbTests
{
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private HermesVerbExecutor CreateExecutor()
    {
        var executor = new HermesVerbExecutor(_options);
        VerbRegistration.RegisterAll(executor, Path.GetTempPath());
        return executor;
    }

    [Fact]
    public void Help_ListsAllVerbs()
    {
        var executor = CreateExecutor();
        
        var input = """{"verb": "help", "arguments": {}}""";
        var result = executor.Execute(input);

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
        
        var verbs = doc.RootElement.GetProperty("verbs");
        Assert.True(verbs.GetArrayLength() > 10, "Should have many registered verbs");
        
        // Check a few known verbs are present
        var verbNames = verbs.EnumerateArray()
            .Select(v => v.GetProperty("name").GetString())
            .ToList();
        
        Assert.Contains("fs.readFile", verbNames);
        Assert.Contains("proc.run", verbNames);
        Assert.Contains("sys.machineInfo", verbNames);
        Assert.Contains("help", verbNames);
    }

    [Fact]
    public void Help_FiltersByVerbName()
    {
        var executor = CreateExecutor();
        
        var input = """{"verb": "help", "arguments": {"verb": "fs.readFile"}}""";
        var result = executor.Execute(input);

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
        
        var verbs = doc.RootElement.GetProperty("verbs");
        Assert.Equal(1, verbs.GetArrayLength());
        Assert.Equal("fs.readFile", verbs[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Help_VerbNotFound_ReturnsFailed()
    {
        var executor = CreateExecutor();
        
        var input = """{"verb": "help", "arguments": {"verb": "nonexistent.verb"}}""";
        var result = executor.Execute(input);

        var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Contains("not found", doc.RootElement.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public void Help_IncludesSchema_WhenRequested()
    {
        var executor = CreateExecutor();
        
        var input = """{"verb": "help", "arguments": {"verb": "fs.exists", "includeSchema": true}}""";
        var result = executor.Execute(input);

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
        
        var verbs = doc.RootElement.GetProperty("verbs");
        Assert.Equal(1, verbs.GetArrayLength());
        
        var verbInfo = verbs[0];
        Assert.True(verbInfo.TryGetProperty("argumentsSchema", out var argsSchema));
        Assert.True(verbInfo.TryGetProperty("resultSchema", out var resultSchema));
        Assert.False(string.IsNullOrEmpty(argsSchema.GetString()));
        Assert.False(string.IsNullOrEmpty(resultSchema.GetString()));
        
        // Verify the schema contains expected content
        Assert.Contains("path", argsSchema.GetString()!);
    }

    [Fact]
    public void Help_OmitsSchema_ByDefault()
    {
        var executor = CreateExecutor();
        
        var input = """{"verb": "help", "arguments": {"verb": "fs.exists"}}""";
        var result = executor.Execute(input);

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
        
        var verbs = doc.RootElement.GetProperty("verbs");
        var verbInfo = verbs[0];
        
        // With JsonIgnoreCondition.WhenWritingNull, null properties should be absent
        Assert.False(verbInfo.TryGetProperty("argumentsSchema", out _));
        Assert.False(verbInfo.TryGetProperty("resultSchema", out _));
    }

    [Fact]
    public void Help_IncludesDescriptions()
    {
        var executor = CreateExecutor();
        
        var input = """{"verb": "help", "arguments": {"verb": "fs.readFile"}}""";
        var result = executor.Execute(input);

        var doc = JsonDocument.Parse(result);
        var verbInfo = doc.RootElement.GetProperty("verbs")[0];
        
        Assert.True(verbInfo.TryGetProperty("description", out var desc));
        var description = desc.GetString();
        Assert.False(string.IsNullOrEmpty(description));
        Assert.NotEqual("No description available.", description);
    }

    [Fact]
    public void Help_VerbNameIsCaseInsensitive()
    {
        var executor = CreateExecutor();
        
        var input = """{"verb": "help", "arguments": {"verb": "FS.READFILE"}}""";
        var result = executor.Execute(input);

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
        
        var verbs = doc.RootElement.GetProperty("verbs");
        Assert.Equal(1, verbs.GetArrayLength());
    }

    [Fact]
    public void Help_VerbsAreSortedAlphabetically()
    {
        var executor = CreateExecutor();
        
        var input = """{"verb": "help", "arguments": {}}""";
        var result = executor.Execute(input);

        var doc = JsonDocument.Parse(result);
        var verbs = doc.RootElement.GetProperty("verbs").EnumerateArray()
            .Select(v => v.GetProperty("name").GetString()!)
            .ToList();
        
        var sorted = verbs.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, verbs);
    }
}
