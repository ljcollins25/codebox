using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Hermes.Core;
using Hermes.Verbs;
using Xunit;

namespace Hermes.Tests;

public class HermesVerbExecutorTests
{
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    [Fact]
    public void Execute_WithValidEnvelope_ReturnsResult()
    {
        var executor = new HermesVerbExecutor(_options);
        executor.Register("test.echo", new TestEchoVerb());

        var input = """{"verb": "test.echo", "arguments": {"message": "hello"}}""";
        var result = executor.Execute(input);

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal("hello", doc.RootElement.GetProperty("echoedMessage").GetString());
    }

    [Fact]
    public void Execute_WithUnknownVerb_ThrowsException()
    {
        var executor = new HermesVerbExecutor(_options);

        var input = """{"verb": "unknown.verb", "arguments": {}}""";
        
        var ex = Assert.Throws<InvalidOperationException>(() => executor.Execute(input));
        Assert.Contains("Unknown VeRB", ex.Message);
    }

    [Fact]
    public void Register_DuplicateVerb_ThrowsException()
    {
        var executor = new HermesVerbExecutor(_options);
        executor.Register("test.echo", new TestEchoVerb());

        Assert.Throws<InvalidOperationException>(() => 
            executor.Register("test.echo", new TestEchoVerb()));
    }

    [Fact]
    public void GetRegisteredVerbs_ReturnsAllRegistered()
    {
        var executor = new HermesVerbExecutor(_options);
        executor.Register("test.echo", new TestEchoVerb());
        executor.Register("test.other", new TestEchoVerb());

        var verbs = executor.GetRegisteredVerbs().ToList();
        
        Assert.Contains("test.echo", verbs);
        Assert.Contains("test.other", verbs);
    }

    [Fact]
    public void Execute_VerbNameIsCaseInsensitive()
    {
        var executor = new HermesVerbExecutor(_options);
        executor.Register("Test.Echo", new TestEchoVerb());

        var input = """{"verb": "TEST.ECHO", "arguments": {"message": "hello"}}""";
        var result = executor.Execute(input);

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean());
    }
}

// Test VeRB for unit testing
public sealed class TestEchoArgs
{
    public required string Message { get; init; }
}

public sealed class TestEchoResult : VerbResult
{
    public required string EchoedMessage { get; init; }
}

public sealed class TestEchoVerb : IVerb<TestEchoArgs, TestEchoResult>
{
    public TestEchoResult Execute(TestEchoArgs args)
    {
        return new TestEchoResult { EchoedMessage = args.Message };
    }
}
