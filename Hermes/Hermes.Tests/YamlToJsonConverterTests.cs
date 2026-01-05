using Hermes.Core;
using Xunit;

namespace Hermes.Tests;

public class YamlToJsonConverterTests
{
    [Fact]
    public void Convert_SimpleYaml_ReturnsJson()
    {
        var yaml = """
            verb: fs.readFile
            arguments:
              path: /tmp/test.txt
            """;

        var json = YamlToJsonConverter.Convert(yaml);

        Assert.Contains("\"verb\"", json);
        Assert.Contains("\"fs.readFile\"", json);
        Assert.Contains("\"arguments\"", json);
        Assert.Contains("\"path\"", json);
    }

    [Fact]
    public void IsLikelyYaml_WithJson_ReturnsFalse()
    {
        var json = """{"verb": "test"}""";

        Assert.False(YamlToJsonConverter.IsLikelyYaml(json));
    }

    [Fact]
    public void IsLikelyYaml_WithYaml_ReturnsTrue()
    {
        var yaml = """
            verb: test
            """;

        Assert.True(YamlToJsonConverter.IsLikelyYaml(yaml));
    }

    [Fact]
    public void NormalizeToJson_WithJson_ReturnsUnchanged()
    {
        var json = """{"verb": "test"}""";

        var result = YamlToJsonConverter.NormalizeToJson(json);

        Assert.Equal(json, result);
    }

    [Fact]
    public void NormalizeToJson_WithYaml_ConvertsToJson()
    {
        var yaml = "verb: test";

        var result = YamlToJsonConverter.NormalizeToJson(yaml);

        Assert.Contains("\"verb\"", result);
        Assert.Contains("\"test\"", result);
    }
}
