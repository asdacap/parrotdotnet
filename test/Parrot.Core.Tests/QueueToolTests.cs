using System.Text.Json;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Security;
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
            ["name", "items", "source_file", "direction", "close"],
            ["name"]);
        await AssertDescriptor(
            QueueTakeTool.Input.Descriptor,
            ["name", "count", "direction", "yield_after_ms"],
            ["name"]);

        using var listen = JsonDocument.Parse(QueueListenTool.Input.Descriptor);
        using var push = JsonDocument.Parse(QueuePushTool.Input.Descriptor);
        using var take = JsonDocument.Parse(QueueTakeTool.Input.Descriptor);
        _ = await Assert.That(listen.RootElement.GetProperty("properties").GetProperty("enabled").GetProperty("default").GetBoolean()).IsTrue();
        var pushProperties = push.RootElement.GetProperty("properties");
        _ = await Assert.That(pushProperties.GetProperty("items").GetProperty("description").GetString()).Contains("Exactly one");
        _ = await Assert.That(pushProperties.GetProperty("source_file").GetProperty("description").GetString()).Contains("Exactly one");
        _ = await Assert.That(pushProperties.GetProperty("direction").GetProperty("default").GetString()).IsEqualTo("back");
        _ = await Assert.That(pushProperties.GetProperty("close").GetProperty("default").GetBoolean()).IsFalse();
        _ = await Assert.That(take.RootElement.GetProperty("properties").GetProperty("count").GetProperty("minimum").GetInt64()).IsEqualTo(1);
        _ = await Assert.That(take.RootElement.GetProperty("properties").GetProperty("yield_after_ms").GetProperty("minimum").GetInt64()).IsEqualTo(0);
    }

    [Test]
    public async Task Queue_take_reports_open_empty_timeout_explicitly(CancellationToken cancellationToken)
    {
        using var queues = TestModels.Queues(AgentIdentity.Main("queue-tool-open", "main"));
        _ = queues.Create("work", string.Empty);
        var result = await new QueueTakeTool(queues).Execute(
            new ToolInvocation("call", "{\"name\":\"work\",\"yield_after_ms\":20}"),
            Selection(),
            cancellationToken);

        using var document = JsonDocument.Parse(result.Text);
        _ = await Assert.That(document.RootElement.GetProperty("closed").GetBoolean()).IsFalse();
        _ = await Assert.That(document.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Queue_push_close_allows_prompt_drain_completion(CancellationToken cancellationToken)
    {
        using var queues = TestModels.Queues(AgentIdentity.Main("queue-tool-close", "main"));
        _ = queues.Create("work", string.Empty);
        var waiting = new QueueTakeTool(queues).Execute(
            new ToolInvocation("take", "{\"name\":\"work\",\"yield_after_ms\":30000}"),
            Selection(),
            cancellationToken);

        _ = await new QueuePushTool(queues, new ToolWorkspace(Environment.CurrentDirectory)).Execute(
            new ToolInvocation("close", "{\"name\":\"work\",\"items\":[],\"close\":true}"),
            Selection(),
            cancellationToken);
        var completed = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
        _ = await Assert.That(completed).IsSameReferenceAs(waiting);
        var result = await waiting;

        using var document = JsonDocument.Parse(result.Text);
        _ = await Assert.That(document.RootElement.GetProperty("closed").GetBoolean()).IsTrue();
        _ = await Assert.That(document.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Queue_push_adds_final_items_closes_idempotently_and_rejects_late_items(
        CancellationToken cancellationToken)
    {
        using var queues = TestModels.Queues(AgentIdentity.Main("queue-tool-final", "main"));
        _ = queues.Create("work", string.Empty);
        var tool = new QueuePushTool(queues, new ToolWorkspace(Environment.CurrentDirectory));

        var closed = await tool.Execute(
            new ToolInvocation("close", "{\"name\":\"work\",\"items\":[\"final\"],\"close\":true}"),
            Selection(),
            cancellationToken);
        var closedAgain = await tool.Execute(
            new ToolInvocation("close-again", "{\"name\":\"work\",\"items\":[],\"close\":true}"),
            Selection(),
            cancellationToken);
        var late = await tool.Execute(
            new ToolInvocation("late", "{\"name\":\"work\",\"items\":[\"late\"]}"),
            Selection(),
            cancellationToken);
        var taken = queues.TryTake("work", 1, Parrot.Queues.QueueDirection.Front);

        using var closedDocument = JsonDocument.Parse(closed.Text);
        using var closedAgainDocument = JsonDocument.Parse(closedAgain.Text);
        _ = await Assert.That(closedDocument.RootElement.GetProperty("closed").GetBoolean()).IsTrue();
        _ = await Assert.That(closedDocument.RootElement.GetProperty("size").GetInt32()).IsEqualTo(1);
        _ = await Assert.That(closedAgainDocument.RootElement.GetProperty("closed").GetBoolean()).IsTrue();
        _ = await Assert.That(string.Join(',', taken.Items)).IsEqualTo("final");
        _ = await Assert.That(late.Text).IsEqualTo("error: queue: 'work' is closed");
    }

    [Test]
    public async Task Queue_push_requires_exactly_one_item_source(CancellationToken cancellationToken)
    {
        using var queues = TestModels.Queues(AgentIdentity.Main("queue-tool-sources", "main"));
        _ = queues.Create("work", string.Empty);
        var tool = new QueuePushTool(queues, new ToolWorkspace(Environment.CurrentDirectory));

        var neither = await tool.Execute(
            new ToolInvocation("neither", "{\"name\":\"work\"}"),
            Selection(),
            cancellationToken);
        var both = await tool.Execute(
            new ToolInvocation(
                "both",
                "{\"name\":\"work\",\"items\":[],\"source_file\":\"items.txt\"}"),
            Selection(),
            cancellationToken);

        _ = await Assert.That(neither.Text)
            .IsEqualTo("error: Tool arguments require exactly one of 'items' or 'source_file'.");
        _ = await Assert.That(both.Text)
            .IsEqualTo("error: Tool arguments require exactly one of 'items' or 'source_file'.");
        _ = await Assert.That(queues.Get("work").Size).IsEqualTo(0);
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
        _ = await Assert.That(() => JsonSerializer.Deserialize(
                "{\"name\":\"work\",\"close\":true}",
                QueueToolJsonContext.Default.QueueInfoToolInput))
            .Throws<JsonException>();
    }

    private static AgentTurnSelection Selection()
    {
        var provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        return new AgentTurnSelection(
            new ModelSelector(model.Selector),
            new ResolvedModelSelection(
                new ModelSelector(model.Selector),
                null,
                model,
                new ModelAliasSnapshot([])),
            TestModels.Profile(),
            SecurityProfile.Compose(readOnly: false, [], [], []));
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
