using Parrot.Context;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class StructuredConversationRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-structured-conversation-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Structured_conversation_reopens_in_order_and_respects_watermark()
    {
        var resources = Resources("conversation");
        using (var database = SessionDatabase.Open(resources.DatabasePath))
        {
            var repository = new EventRepository(database);
            repository.AppendConversation(
                Published("assistant"),
                ConversationOrigin.Model,
                LLMRole.Assistant,
                [ConversationPart.TextPart(string.Empty)],
                [new LLMToolCall("call-1", "read_image", "{}")],
                string.Empty);
            repository.AppendConversation(
                Published("tool"),
                ConversationOrigin.Tool,
                LLMRole.Tool,
                [ConversationPart.TextPart("read image")],
                [],
                "call-1");
            repository.AppendConversation(
                Published("images"),
                ConversationOrigin.Tool,
                LLMRole.User,
                [ConversationPart.ImageArtifact(Image())],
                [],
                string.Empty);
        }

        using var reopened = SessionDatabase.Open(resources.DatabasePath);
        var restored = new EventRepository(reopened).Conversation("agent");
        _ = await Assert.That(restored).Count().IsEqualTo(3);
        _ = await Assert.That(restored[0].ToolCalls.Single().Id).IsEqualTo("call-1");
        _ = await Assert.That(restored[1].ToolCallId).IsEqualTo("call-1");
        _ = await Assert.That(restored[2].Parts.Single().ArtifactId).IsEqualTo(Image().ArtifactId);
        _ = await Assert.That(new EventRepository(reopened).ConversationAfter("agent", restored[1].Sequence))
            .HasSingleItem();
    }

    [Test]
    public async Task Agent_history_orders_structured_messages_and_retains_compactions()
    {
        var resources = Resources("history");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database);
        repository.AppendConversation(
            Published("assistant"),
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart("before")],
            [new LLMToolCall("call-1", "read", "{\"path\":\"file\"}")],
            string.Empty);
        _ = repository.SaveCompaction("agent", new CompactionSnapshot("summary one", 1));
        repository.AppendConversation(
            Published("tool"),
            ConversationOrigin.Tool,
            LLMRole.Tool,
            [ConversationPart.TextPart("result")],
            [],
            "call-1");
        _ = repository.SaveCompaction("agent", new CompactionSnapshot("summary two", 2));

        var history = repository.AgentHistory("agent");

        _ = await Assert.That(history).Count().IsEqualTo(4);
        _ = await Assert.That(string.Join(',', history.Select(entry => entry.GetType().Name)))
            .IsEqualTo("AgentHistoryMessageEntry,AgentHistoryCompactionEntry,"
                + "AgentHistoryMessageEntry,AgentHistoryCompactionEntry");
        var message = (AgentHistoryMessageEntry)history[0];
        _ = await Assert.That(message.Role).IsEqualTo("assistant");
        _ = await Assert.That(message.ToolCalls.Single().ArgumentsJson).Contains("file");
        var compactions = history.OfType<AgentHistoryCompactionEntry>().ToArray();
        _ = await Assert.That(string.Join(',', compactions.Select(entry => entry.Summary)))
            .IsEqualTo("summary one,summary two");
        _ = await Assert.That(repository.SaveCompaction("agent", new CompactionSnapshot("duplicate", 2))).IsFalse();
    }

    [Test]
    public async Task Compaction_status_is_atomic_and_canonical_history_excludes_associated_statuses()
    {
        var resources = Resources("compaction-status");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database);
        repository.AppendConversation(
            Published("before"),
            ConversationOrigin.Model,
            LLMRole.User,
            [ConversationPart.TextPart("before")],
            [],
            string.Empty);
        var firstStatus = Published("first-status");
        _ = await Assert.That(repository.AppendCompactionStatus(
            firstStatus,
            new CompactionSnapshot("first summary", 0),
            "first status")).IsTrue();
        repository.AppendConversation(
            Published("between"),
            ConversationOrigin.Model,
            LLMRole.User,
            [ConversationPart.TextPart("between")],
            [],
            string.Empty);
        var reminder = Published("reminder");
        _ = await Assert.That(repository.AppendContextReminder(
            reminder,
            new ContextReminderCheckpoint("provider/model", 10_000, 75),
            "reminder")).IsTrue();
        _ = await Assert.That(repository.LatestContextReminder("agent")).IsNotNull();
        var secondStatus = Published("second-status");
        _ = await Assert.That(repository.AppendCompactionStatus(
            secondStatus,
            new CompactionSnapshot("second summary", 3),
            "second status")).IsTrue();
        _ = await Assert.That(repository.LatestContextReminder("agent")).IsNull();
        repository.AppendConversation(
            Published("after"),
            ConversationOrigin.Model,
            LLMRole.User,
            [ConversationPart.TextPart("after")],
            [],
            string.Empty);

        var context = repository.CompactionHistory("agent")
            ?? throw new InvalidOperationException("Expected compaction history.");

        _ = await Assert.That(firstStatus.PayloadCase).IsEqualTo(Event.PayloadOneofCase.StatusInjected);
        _ = await Assert.That(secondStatus.PayloadCase).IsEqualTo(Event.PayloadOneofCase.StatusInjected);
        _ = await Assert.That(context.Snapshot.Summary).IsEqualTo("second summary");
        _ = await Assert.That(context.Status?.Parts.Single().Text).IsEqualTo("second status");
        _ = await Assert.That(string.Join(',', context.Tail.Select(item => item.Parts.Single().Text)))
            .IsEqualTo("reminder,after");
        _ = await Assert.That(string.Join(',', repository.Replay().Select(published => published.Id)))
            .Contains("first-status")
            .And.Contains("second-status");
        _ = await Assert.That(repository.AppendCompactionStatus(
            Published("duplicate"),
            new CompactionSnapshot("duplicate", 3),
            "duplicate status")).IsFalse();
    }

    [Test]
    public async Task Tool_terminal_is_idempotent_and_recovers_structured_parts()
    {
        var resources = Resources("terminals");
        using (var database = SessionDatabase.Open(resources.DatabasePath))
        {
            var repository = new EventRepository(database);
            var terminal = new ToolExecutionTerminal(
                "call-1",
                "read_image",
                ToolExecutionStatus.Finished,
                [ConversationPart.TextPart("done"), ConversationPart.ImageArtifact(Image())],
                string.Empty);
            _ = await Assert.That(repository.AppendToolTerminal(Published("first"), terminal)).IsTrue();
            _ = await Assert.That(repository.AppendToolTerminal(Published("replay"), terminal)).IsFalse();
            _ = await Assert.That(repository.Replay()).Count().IsEqualTo(1);
        }

        using var reopened = SessionDatabase.Open(resources.DatabasePath);
        var restored = new EventRepository(reopened).ToolTerminals("agent").Single();
        _ = await Assert.That(restored.ToolCallId).IsEqualTo("call-1");
        _ = await Assert.That(restored.Status).IsEqualTo(ToolExecutionStatus.Finished);
        _ = await Assert.That(string.Join(',', restored.ResultParts.Select(part => part.Kind)))
            .IsEqualTo("Text,ImageArtifact");
    }

    [Test]
    public async Task Checkpoint_fork_copies_complete_named_history_and_remaps_tool_settlement()
    {
        var resources = Resources("checkpoint-fork");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database);
        repository.AppendConversation(
            Published("before"),
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("before")],
            [],
            string.Empty);
        repository.AppendConversation(
            Published("checkpoint-call"),
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(string.Empty)],
            [new LLMToolCall("checkpoint", "set_checkpoint", "{\"title\":\"handoff\"}")],
            string.Empty);
        var checkpointAssistant = repository.Conversation("agent")[1].Sequence;
        _ = repository.RecordCheckpoint("agent", "handoff", checkpointAssistant, "checkpoint");
        _ = repository.AppendToolSettlement(
            Published("checkpoint-result"),
            checkpointAssistant,
            new ToolExecutionTerminal(
                "checkpoint",
                "set_checkpoint",
                ToolExecutionStatus.Finished,
                [ConversationPart.TextPart("handoff")],
                "handoff"));
        repository.AppendConversation(
            Published("between"),
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("between")],
            [],
            string.Empty);
        repository.AppendConversation(
            Published("spawn-call"),
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(string.Empty)],
            [new LLMToolCall("spawn", "agent_spawn", "{}")],
            string.Empty);
        var spawnAssistant = repository.Conversation("agent")[^1].Sequence;

        repository.InitializeForkedAgentHistory(
            "agent",
            "child",
            spawnAssistant,
            "spawn",
            HistoryForkSelection.Parse("handoff"));

        var child = repository.Conversation("child");
        _ = await Assert.That(string.Join(',', child.Select(item => item.Role)))
            .IsEqualTo("Assistant,Tool,User");
        _ = await Assert.That(child[0].ToolCalls.Single().Name).IsEqualTo("set_checkpoint");
        _ = await Assert.That(child[1].Parts.Single().Text).IsEqualTo("handoff");
        _ = await Assert.That(child[2].Parts.Single().Text).IsEqualTo("between");
        _ = await Assert.That(repository.ToolTerminals("child").Single().ToolName)
            .IsEqualTo("set_checkpoint");
        _ = await Assert.That(repository.LatestUsableCheckpoint("child", "handoff", long.MaxValue))
            .IsNotNull();
    }

    [Test]
    public async Task Latest_duplicate_checkpoint_does_not_fall_back_when_it_is_the_current_batch()
    {
        var resources = Resources("checkpoint-latest");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database);
        foreach (var call in new[] { "first", "latest" })
        {
            repository.AppendConversation(
                Published($"{call}-assistant"),
                ConversationOrigin.Model,
                LLMRole.Assistant,
                [ConversationPart.TextPart(string.Empty)],
                [new LLMToolCall(call, "set_checkpoint", "{\"title\":\"same\"}")],
                string.Empty);
            var assistant = repository.Conversation("agent")[^1].Sequence;
            _ = repository.RecordCheckpoint("agent", "same", assistant, call);
            if (string.Equals(call, "first", StringComparison.Ordinal))
            {
                _ = repository.AppendToolSettlement(
                    Published("first-result"),
                    assistant,
                    new ToolExecutionTerminal(
                        call,
                        "set_checkpoint",
                        ToolExecutionStatus.Finished,
                        [ConversationPart.TextPart("same")],
                        "same"));
            }
        }

        var current = repository.Conversation("agent")[^1].Sequence;
        _ = await Assert.That(() => repository.InitializeForkedAgentHistory(
            "agent",
            "child",
            current,
            "latest",
            HistoryForkSelection.Parse("same"))).Throws<ArgumentException>();
        _ = await Assert.That(repository.Conversation("child")).IsEmpty();
    }

    [Test]
    public async Task Materialize_reads_durable_image_only_at_provider_boundary(CancellationToken cancellationToken)
    {
        var resources = Resources("materialize");
        var store = new ImageArtifactStore(resources);
        await using var source = new MemoryStream(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="));
        var artifact = await store.Persist(source, "pixel.png", "test", cancellationToken);
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database, store);
        _ = repository.RecordImageArtifact(artifact, "upload-1");

        var contents = repository.Materialize(
            [ConversationPart.TextPart("before"), ConversationPart.ImageArtifact(artifact)]);

        _ = await Assert.That(string.Join(',', contents.Select(content => content.Kind)))
            .IsEqualTo("Text,Image");
        _ = await Assert.That(contents[1].Image).IsNotEmpty();
        _ = await Assert.That(contents[1].MediaType).IsEqualTo("image/png");
    }

    private static Event Published(string id) => new() { Id = id, AgentSessionId = "agent" };

    private static ImageArtifactMetadata Image() => new(
        new string('a', 64), new string('a', 64), "image/png", 1, 1, 1, 1, 1, "pixel.png", "test");

    private UserSessionResources Resources(string id) => new(
        new StatePaths(_root, _root, _root),
        UserSessionId.Parse(id),
        ProjectWorkspace.FromLaunchDirectory(Directory.CreateDirectory(Path.Combine(_root, "work")).FullName));
}
