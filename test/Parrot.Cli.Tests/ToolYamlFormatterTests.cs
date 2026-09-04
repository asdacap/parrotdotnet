using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class ToolYamlFormatterTests
{
    [Test]
    public async Task Converts_complete_json_values_to_yaml()
    {
        var values = new (string Json, string Yaml)[]
        {
            ("{\"value\":1,\"text\":\"true\"}", "value: 1\ntext: \"true\""),
            ("[1,true,null]", "- 1\n- true\n- null"),
            ("\"hello\"", "\"hello\""),
            ("42", "42"),
            ("true", "true"),
            ("null", "null"),
        };

        foreach ((var json, var expectedYaml) in values)
        {
            _ = await Assert.That(ToolYamlFormatter.TryFormat(json, out var yaml)).IsTrue();
            _ = await Assert.That(yaml).IsEqualTo(expectedYaml);
        }
    }

    [Test]
    public async Task Rejects_invalid_trailing_mixed_and_duplicate_json_values()
    {
        var values = new[]
        {
            "not json",
            "{\"value\":1} trailing",
            "true false",
            "{\"value\":1,\"value\":2}",
        };

        foreach (var value in values)
        {
            _ = await Assert.That(ToolYamlFormatter.TryFormat(value, out var formatted)).IsFalse();
            _ = await Assert.That(formatted).IsEqualTo(value);
            _ = await Assert.That(ToolYamlFormatter.Format(value)).IsEqualTo(value);
        }
    }
}
