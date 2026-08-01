using System.Text.Json;
using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class TodoToolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-todo-tool-tests", Guid.NewGuid().ToString("n"));

    private readonly EventBroker _events = new();

    public void Dispose()
    {
        _events.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Replacement_is_normalized_ordered_durable_and_scoped(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "session.db");
        string firstId;

        using (var database = SessionDatabase.Open(path))
        {
            var repository = new EventRepository(database);
            var first = Session(repository, "first");
            var second = Session(repository, "second");
            var write = new TodoWriteTool(first);
            var read = new TodoReadTool(first);

            const string argumentsJson =
                """
                {"todos":[
                  {"content":"  investigate  ","status":"in_progress","priority":"high"},
                  {"id":"kept","content":"fix","status":"pending","priority":"low"}
                ]}
                """;
            var written = (await write.Execute(
                new ToolInvocation("test-call", argumentsJson),
                Selection(),
                cancellationToken)).Text;
            using var normalized = JsonDocument.Parse(written);
            var items = normalized.RootElement;
            firstId = items[0].GetProperty("id").GetString() ?? string.Empty;

            _ = await Assert.That(items.GetArrayLength()).IsEqualTo(2);
            _ = await Assert.That(firstId).StartsWith("todo-");
            _ = await Assert.That(items[0].GetProperty("content").GetString()).IsEqualTo("investigate");
            _ = await Assert.That(items[0].GetProperty("position").GetInt32()).IsEqualTo(0);
            _ = await Assert.That(items[1].GetProperty("id").GetString()).IsEqualTo("kept");
            _ = await Assert.That(items[1].GetProperty("position").GetInt32()).IsEqualTo(1);
            _ = await Assert.That((await read.Execute(new ToolInvocation("test-call", "{}"), Selection(), cancellationToken)).Text).IsEqualTo(written);
            _ = await Assert.That((await new TodoReadTool(second).Execute(new ToolInvocation("test-call", "{}"), Selection(), cancellationToken)).Text).IsEqualTo("[]");

            var updated = repository.Replay().Single();
            _ = await Assert.That(updated.AgentSessionId).IsEqualTo("first");
            _ = await Assert.That(updated.TodoUpdated.Todos).Count().IsEqualTo(2);
            _ = await Assert.That(updated.TodoUpdated.Todos[0].Content).IsEqualTo("investigate");
        }

        using var reopened = SessionDatabase.Open(path);
        var reopenedSession = Session(new EventRepository(reopened), "first");
        var persisted = (await new TodoReadTool(reopenedSession).Execute(new ToolInvocation("test-call", "{}"), Selection(), cancellationToken)).Text;

        _ = await Assert.That(persisted).Contains(firstId);
        _ = await Assert.That(persisted).Contains("\"position\":1");
        _ = await Assert.That((await new TodoWriteTool(reopenedSession).Execute(
            new ToolInvocation(
                "test-call",
                """{"todos":[]}"""),
            Selection(),
            cancellationToken)).Text).IsEqualTo("[]");
        _ = await Assert.That((await new TodoReadTool(reopenedSession).Execute(
            new ToolInvocation(
                "test-call",
                "{}"),
            Selection(),
            cancellationToken)).Text).IsEqualTo("[]");
    }

    [Test]
    [Arguments("{\"todos\":[{\"content\":\"  \",\"status\":\"pending\",\"priority\":\"low\"}]}")]
    [Arguments("{\"todos\":[{\"content\":\"bad\",\"status\":\"unknown\",\"priority\":\"low\"}]}")]
    [Arguments("{\"todos\":[{\"content\":\"bad\",\"status\":\"pending\",\"priority\":\"unknown\"}]}")]
    [Arguments("{\"todos\":[{\"id\":\"same\",\"content\":\"one\",\"status\":\"pending\",\"priority\":\"low\"},{\"id\":\"same\",\"content\":\"two\",\"status\":\"pending\",\"priority\":\"low\"}]}")]
    [Arguments("{\"todos\":[],\"unexpected\":true}")]
    public async Task Invalid_replacement_is_rejected_without_changing_the_list(
        string argumentsJson, CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var session = Session(repository, "session");
        var write = new TodoWriteTool(session);
        _ = (await write.Execute(
            new ToolInvocation(
                "test-call",
                """{"todos":[{"id":"kept","content":"original","status":"pending","priority":"medium"}]}"""),
            Selection(),
            cancellationToken)).Text;

        var result = (await write.Execute(new ToolInvocation("test-call", argumentsJson), Selection(), cancellationToken)).Text;
        var current = (await new TodoReadTool(session).Execute(new ToolInvocation("test-call", "{}"), Selection(), cancellationToken)).Text;

        _ = await Assert.That(result).StartsWith("error:");
        _ = await Assert.That(current).Contains("original");
        _ = await Assert.That(current).Contains("kept");
        _ = await Assert.That(repository.Replay()).Count().IsEqualTo(1);
    }

    private static AgentTurnSelection Selection()
    {
        var provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        return new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            TestModels.Profile(),
            SecurityProfile.Compose(readOnly: false, [], [], []));
    }

    private AgentSession Session(EventRepository repository, string sessionId)
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var identity = AgentIdentity.Main(sessionId, string.Empty);
        var dependencies = TestModels.Dependencies(identity, _events, repository, CancellationToken.None);
        return new AgentSession(
            identity,
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            _events,
            repository,
            [],
            TestModels.MaterializePrompt(identity, "/workspace", "/workspace"),
            new TodoCollection(sessionId, repository, _events),
            new ToolOutputBlobStore(_root),
            new Compactor(120_000, 60_000, 1024),
            dependencies.ActiveWorkReminder,
            dependencies.Profile,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            dependencies.Status,
            dependencies.Registry,
            dependencies.Queues,
            CancellationToken.None);
    }
}
