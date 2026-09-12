using System.Text.Json;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class QueueToolTests
{
    [Test]
    public async Task Queue_take_reports_open_empty_timeout_explicitly(CancellationToken cancellationToken)
    {
        await using var queueFixture = TestModels.Queues(AgentIdentity.Main("queue-tool-open", "main", TestModels.PromptTemplates));
        var queues = queueFixture.Queues;
        _ = queues.Create("work", string.Empty);
        ITool tool = new QueueTakeTool(queues, TestDiagnosticLog.Instance);
        var result = await tool.Execute(
            new ToolInvocation("call", "{\"name\":\"work\",\"yield_after_ms\":20}"),
            new SelectionFixture().Selection,
            cancellationToken);

        using var document = JsonDocument.Parse(result.Text);
        _ = await Assert.That(document.RootElement.GetProperty("closed").GetBoolean()).IsFalse();
        _ = await Assert.That(document.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Queue_push_close_allows_prompt_drain_completion(CancellationToken cancellationToken)
    {
        await using var queueFixture = TestModels.Queues(AgentIdentity.Main("queue-tool-close", "main", TestModels.PromptTemplates));
        var queues = queueFixture.Queues;
        _ = queues.Create("work", string.Empty);
        ITool takeTool = new QueueTakeTool(queues, TestDiagnosticLog.Instance);
        var waiting = takeTool.Execute(
            new ToolInvocation("take", "{\"name\":\"work\",\"yield_after_ms\":30000}"),
            new SelectionFixture().Selection,
            cancellationToken);

        ITool pushTool = new QueuePushTool(queues, new ToolWorkspace(Environment.CurrentDirectory));
        _ = await pushTool.Execute(
            new ToolInvocation("close", "{\"name\":\"work\",\"items\":[],\"close\":true}"),
            new SelectionFixture().Selection,
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
        await using var queueFixture = TestModels.Queues(AgentIdentity.Main("queue-tool-final", "main", TestModels.PromptTemplates));
        var queues = queueFixture.Queues;
        _ = queues.Create("work", string.Empty);
        ITool tool = new QueuePushTool(queues, new ToolWorkspace(Environment.CurrentDirectory));

        var closed = await tool.Execute(
            new ToolInvocation("close", "{\"name\":\"work\",\"items\":[\"final\"],\"close\":true}"),
            new SelectionFixture().Selection,
            cancellationToken);
        var closedAgain = await tool.Execute(
            new ToolInvocation("close-again", "{\"name\":\"work\",\"items\":[],\"close\":true}"),
            new SelectionFixture().Selection,
            cancellationToken);
        var late = await tool.Execute(
            new ToolInvocation("late", "{\"name\":\"work\",\"items\":[\"late\"]}"),
            new SelectionFixture().Selection,
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
        await using var queueFixture = TestModels.Queues(AgentIdentity.Main("queue-tool-sources", "main", TestModels.PromptTemplates));
        var queues = queueFixture.Queues;
        _ = queues.Create("work", string.Empty);
        ITool tool = new QueuePushTool(queues, new ToolWorkspace(Environment.CurrentDirectory));

        var neither = await tool.Execute(
            new ToolInvocation("neither", "{\"name\":\"work\"}"),
            new SelectionFixture().Selection,
            cancellationToken);
        var both = await tool.Execute(
            new ToolInvocation(
                "both",
                "{\"name\":\"work\",\"items\":[],\"source_file\":\"items.txt\"}"),
            new SelectionFixture().Selection,
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

    private sealed class SelectionFixture
    {
        public SelectionFixture()
        {
            ILLMProvider provider = new UnusedProvider();
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            Selection = new AgentTurnSelection(
                new ModelSelector(model.Selector),
                new ResolvedModelSelection(
                    new ModelSelector(model.Selector),
                    null,
                    model,
                    new ModelRoutingSnapshot(model.Selector, new ModelAliasSnapshot([]), 0)),
                new TestProfileFixture().Mode,
                SecurityProfile.Compose(readOnly: false, [], [], []));
        }

        public AgentTurnSelection Selection { get; }
    }
}
