using System.Text.Json;
using Parrot.Agent;
using Parrot.Queues;
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
        ITool tool = new QueueTakeTool(queues, new ResourceResolverFixture(null, null), TestDiagnosticLog.Instance);
        var result = await tool.Execute(
            new ToolInvocation("call", "{\"name\":\"work\",\"yield_after_ms\":20}"),
            TestTurnSelection.Create(SecurityProfile.Compose(readOnly: false, [], [], [])),
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
        ITool takeTool = new QueueTakeTool(queues, new ResourceResolverFixture(null, null), TestDiagnosticLog.Instance);
        var waiting = takeTool.Execute(
            new ToolInvocation("take", "{\"name\":\"work\",\"yield_after_ms\":30000}"),
            TestTurnSelection.Create(SecurityProfile.Compose(readOnly: false, [], [], [])),
            cancellationToken);

        ITool pushTool = new QueuePushTool(queues, new ResourceResolverFixture(null, null), new ToolWorkspace(Environment.CurrentDirectory), TestDiagnosticLog.Instance);
        _ = await pushTool.Execute(
            new ToolInvocation("close", "{\"name\":\"work\",\"items\":[],\"close\":true}"),
            TestTurnSelection.Create(SecurityProfile.Compose(readOnly: false, [], [], [])),
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
        ITool tool = new QueuePushTool(queues, new ResourceResolverFixture(null, null), new ToolWorkspace(Environment.CurrentDirectory), TestDiagnosticLog.Instance);

        var closed = await tool.Execute(
            new ToolInvocation("close", "{\"name\":\"work\",\"items\":[\"final\"],\"close\":true}"),
            TestTurnSelection.Create(SecurityProfile.Compose(readOnly: false, [], [], [])),
            cancellationToken);
        var closedAgain = await tool.Execute(
            new ToolInvocation("close-again", "{\"name\":\"work\",\"items\":[],\"close\":true}"),
            TestTurnSelection.Create(SecurityProfile.Compose(readOnly: false, [], [], [])),
            cancellationToken);
        var late = await tool.Execute(
            new ToolInvocation("late", "{\"name\":\"work\",\"items\":[\"late\"]}"),
            TestTurnSelection.Create(SecurityProfile.Compose(readOnly: false, [], [], [])),
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
        ITool tool = new QueuePushTool(queues, new ResourceResolverFixture(null, null), new ToolWorkspace(Environment.CurrentDirectory), TestDiagnosticLog.Instance);

        var neither = await tool.Execute(
            new ToolInvocation("neither", "{\"name\":\"work\"}"),
            TestTurnSelection.Create(SecurityProfile.Compose(readOnly: false, [], [], [])),
            cancellationToken);
        var both = await tool.Execute(
            new ToolInvocation(
                "both",
                "{\"name\":\"work\",\"items\":[],\"source_file\":\"items.txt\"}"),
            TestTurnSelection.Create(SecurityProfile.Compose(readOnly: false, [], [], [])),
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

    [Test]
    public async Task Qualified_queue_operations_use_target_store_and_bare_names_stay_local(CancellationToken cancellationToken)
    {
        await using var localFixture = TestModels.Queues(AgentIdentity.Main("local-path", "local", TestModels.PromptTemplates));
        var local = localFixture.Queues;
        var targetIdentity = AgentIdentity.Main("target-path", "target", TestModels.PromptTemplates);
        await using var children = new ChildRegistry(targetIdentity, QueueChildAdmissionValidator.Validate);
        using IAgentQueues target = new AgentQueues(targetIdentity, local, TestModels.Resources(), children, static identity => new QueueInventory(identity), TestDiagnosticLog.Instance);
        target.Initialize();
        _ = local.Create("work", "local");
        _ = local.Create("local-only", string.Empty);
        _ = target.Local.Create("work", "target");
        var resolver = new ResourceResolverFixture(target, null);
        var selection = TestTurnSelection.Create(SecurityProfile.Compose(readOnly: false, [], [], []));
        ITool info = new QueueInfoTool(local, resolver);
        ITool push = new QueuePushTool(local, resolver, new ToolWorkspace(Environment.CurrentDirectory), TestDiagnosticLog.Instance);
        ITool take = new QueueTakeTool(local, resolver, TestDiagnosticLog.Instance);

        var qualifiedInfo = await info.Execute(new ToolInvocation("info", "{\"name\":\"/root/target/work\"}"), selection, cancellationToken);
        var bareInfo = await info.Execute(new ToolInvocation("bare", "{\"name\":\"work\"}"), selection, cancellationToken);
        var pushed = await push.Execute(new ToolInvocation("push", "{\"name\":\"parent/target/work\",\"items\":[\"target-item\"],\"close\":true}"), selection, cancellationToken);
        var taken = await take.Execute(new ToolInvocation("take", "{\"name\":\"parent/parent/target/work\",\"yield_after_ms\":0}"), selection, cancellationToken);
        var missing = await info.Execute(new ToolInvocation("missing", "{\"name\":\"target/local-only\"}"), selection, cancellationToken);

        _ = await Assert.That(qualifiedInfo.Text).Contains("\"description\":\"target\"");
        _ = await Assert.That(bareInfo.Text).Contains("\"description\":\"local\"");
        _ = await Assert.That(pushed.Text).Contains("\"size\":1");
        _ = await Assert.That(taken.Text).Contains("\"items\":[\"target-item\"]");
        _ = await Assert.That(local.Get("work").Size).IsEqualTo(0);
        _ = await Assert.That(local.Get("work").Closed).IsFalse();
        _ = await Assert.That(target.Get("work").Closed).IsTrue();
        _ = await Assert.That(missing.Text).StartsWith("error: queue:");
    }

    [Test]
    public async Task Qualified_queue_operations_return_resolution_errors(CancellationToken cancellationToken)
    {
        await using var fixture = TestModels.Queues(AgentIdentity.Main("path-error", "main", TestModels.PromptTemplates));
        var resolver = new ResourceResolverFixture(null, null);
        var selection = TestTurnSelection.Create(SecurityProfile.Compose(readOnly: false, [], [], []));
        ITool[] tools =
        [
            new QueueInfoTool(fixture.Queues, resolver),
            new QueuePushTool(fixture.Queues, resolver, new ToolWorkspace(Environment.CurrentDirectory), TestDiagnosticLog.Instance),
            new QueueTakeTool(fixture.Queues, resolver, TestDiagnosticLog.Instance),
        ];
        string[] arguments = ["{\"name\":\"missing/work\"}", "{\"name\":\"missing/work\",\"items\":[]}", "{\"name\":\"missing/work\",\"yield_after_ms\":0}"];
        for (var index = 0; index < tools.Length; index++)
        {
            var result = await tools[index].Execute(new ToolInvocation("call", arguments[index]), selection, cancellationToken);
            _ = await Assert.That(result.Text).IsEqualTo("error: resource owner unavailable");
        }
    }
}
