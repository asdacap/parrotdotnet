using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class ToolYamlFormatterTests
{
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
