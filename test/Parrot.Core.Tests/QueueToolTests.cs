using System.Text.Json;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class QueueToolTests
{
    [Test]
    public async Task Generated_descriptors_preserve_queue_contracts_and_describe_every_field()
    {
        await AssertDescriptor(
            QueueCreateTool.Input.Descriptor,
            ["name", "description"],
            ["name"]);
        await AssertDescriptor(QueueInfoTool.Input.Descriptor, ["name"], ["name"]);
        await AssertDescriptor(QueueListenTool.Input.Descriptor, ["name", "enabled"], ["name"]);
        await AssertDescriptor(
            QueuePushTool.Input.Descriptor,
            ["name", "items", "direction"],
            ["name", "items"]);
        await AssertDescriptor(
            QueueTakeTool.Input.Descriptor,
            ["name", "count", "direction", "yield_after_ms"],
            ["name"]);

        using var listen = JsonDocument.Parse(QueueListenTool.Input.Descriptor);
        using var push = JsonDocument.Parse(QueuePushTool.Input.Descriptor);
        using var take = JsonDocument.Parse(QueueTakeTool.Input.Descriptor);
        _ = await Assert.That(listen.RootElement.GetProperty("properties").GetProperty("enabled").GetProperty("default").GetBoolean()).IsTrue();
        _ = await Assert.That(push.RootElement.GetProperty("properties").GetProperty("direction").GetProperty("default").GetString()).IsEqualTo("back");
        _ = await Assert.That(take.RootElement.GetProperty("properties").GetProperty("count").GetProperty("minimum").GetInt64()).IsEqualTo(1);
        _ = await Assert.That(take.RootElement.GetProperty("properties").GetProperty("yield_after_ms").GetProperty("minimum").GetInt64()).IsEqualTo(0);
    }

    [Test]
    public async Task Distinct_typed_contexts_reject_fields_from_other_queue_tools()
    {
        _ = await Assert.That(() => JsonSerializer.Deserialize(
                "{\"name\":\"work\",\"items\":[]}",
                QueueToolJsonContext.Default.QueueInfoToolInput))
            .Throws<JsonException>();
        _ = await Assert.That(() => JsonSerializer.Deserialize(
                "{\"name\":\"work\",\"enabled\":true}",
                QueueToolJsonContext.Default.QueueCreateToolInput))
            .Throws<JsonException>();
    }

    private static async Task AssertDescriptor(string descriptor, string[] propertyNames, string[] requiredNames)
    {
        using var document = JsonDocument.Parse(descriptor);
        var root = document.RootElement;
        _ = await Assert.That(root.GetProperty("type").GetString()).IsEqualTo("object");
        _ = await Assert.That(root.GetProperty("additionalProperties").GetBoolean()).IsFalse();
        var properties = root.GetProperty("properties");
        _ = await Assert.That(string.Join(',', properties.EnumerateObject().Select(static property => property.Name))).IsEqualTo(string.Join(',', propertyNames));
        _ = await Assert.That(string.Join(',', root.GetProperty("required").EnumerateArray().Select(static item => item.GetString()))).IsEqualTo(string.Join(',', requiredNames));

        foreach (var property in properties.EnumerateObject())
        {
            _ = await Assert.That(property.Value.GetProperty("description").GetString()).IsNotNull().And.IsNotEmpty();
        }
    }
}
