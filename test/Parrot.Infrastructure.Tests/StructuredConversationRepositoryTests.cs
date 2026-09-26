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
                new Event { Id = "assistant", AgentSessionId = "agent" },
                ConversationOrigin.Model,
                LLMRole.Assistant,
                [ConversationPart.TextPart(string.Empty)],
                [new LLMToolCall("call-1", "read_image", "{}")],
                string.Empty);
            repository.AppendConversation(
                new Event { Id = "tool", AgentSessionId = "agent" },
                ConversationOrigin.Tool,
                LLMRole.Tool,
                [ConversationPart.TextPart("read image")],
                [],
                "call-1");
            repository.AppendConversation(
                new Event { Id = "images", AgentSessionId = "agent" },
                ConversationOrigin.Tool,
                LLMRole.User,
                [ConversationPart.ImageArtifact(new ImageArtifactMetadata(new string('a', 64), new string('a', 64), "image/png", 1, 1, 1, 1, 1, "pixel.png", "test"))],
                [],
                string.Empty);
        }

        using var reopened = SessionDatabase.Open(resources.DatabasePath);
        var restored = new EventRepository(reopened).Conversation("agent");
        _ = await Assert.That(restored).Count().IsEqualTo(3);
        _ = await Assert.That(restored[0].ToolCalls.Single().Id).IsEqualTo("call-1");
        _ = await Assert.That(restored[1].ToolCallId).IsEqualTo("call-1");
        _ = await Assert.That(restored[2].Parts.Single().ArtifactId).IsEqualTo(new ImageArtifactMetadata(new string('a', 64), new string('a', 64), "image/png", 1, 1, 1, 1, 1, "pixel.png", "test").ArtifactId);
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
            new Event { Id = "assistant", AgentSessionId = "agent" },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart("before")],
            [new LLMToolCall("call-1", "read", "{\"path\":\"file\"}")],
            string.Empty);
        _ = repository.SaveCompaction("agent", new CompactionSnapshot("summary one", 1));
        repository.AppendConversation(
            new Event { Id = "tool", AgentSessionId = "agent" },
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
            new Event { Id = "before", AgentSessionId = "agent" },
            ConversationOrigin.Model,
            LLMRole.User,
            [ConversationPart.TextPart("before")],
            [],
            string.Empty);
        var firstStatus = new Event { Id = "first-status", AgentSessionId = "agent" };
        _ = await Assert.That(repository.AppendCompactionStatus(
            firstStatus,
            new CompactionSnapshot("first summary", 0),
            "first status")).IsTrue();
        repository.AppendConversation(
            new Event { Id = "between", AgentSessionId = "agent" },
            ConversationOrigin.Model,
            LLMRole.User,
            [ConversationPart.TextPart("between")],
            [],
            string.Empty);
        var reminder = new Event { Id = "reminder", AgentSessionId = "agent" };
        _ = await Assert.That(repository.AppendContextReminder(
            reminder,
            new ContextReminderCheckpoint("provider/model", 10_000, 75),
            77,
            "reminder")).IsTrue();
        _ = await Assert.That(repository.LatestContextReminder("agent")).IsNotNull();
        var secondStatus = new Event { Id = "second-status", AgentSessionId = "agent" };
        _ = await Assert.That(repository.AppendCompactionStatus(
            secondStatus,
            new CompactionSnapshot("second summary", 3),
            "second status")).IsTrue();
        _ = await Assert.That(repository.LatestContextReminder("agent")).IsNull();
        repository.AppendConversation(
            new Event { Id = "after", AgentSessionId = "agent" },
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
            new Event { Id = "duplicate", AgentSessionId = "agent" },
            new CompactionSnapshot("duplicate", 3),
            "duplicate status")).IsFalse();
    }

    [Test]
    [Arguments(ToolExecutionStatus.Finished)]
    [Arguments(ToolExecutionStatus.ImageBudgetExceeded)]
    public async Task Tool_terminal_is_idempotent_and_recovers_structured_parts(ToolExecutionStatus status)
    {
        var resources = Resources("terminals");
        using (var database = SessionDatabase.Open(resources.DatabasePath))
        {
            var repository = new EventRepository(database);
            ConversationPart[] parts = status == ToolExecutionStatus.Finished
                ? [ConversationPart.TextPart("done"), ConversationPart.ImageArtifact(new ImageArtifactMetadata(new string('a', 64), new string('a', 64), "image/png", 1, 1, 1, 1, 1, "pixel.png", "test"))]
                : [ConversationPart.TextPart("budget exceeded")];
            var terminal = new ToolExecutionTerminal(
                "call-1",
                "read_image",
                status,
                parts,
                string.Empty);
            _ = await Assert.That(repository.AppendToolTerminal(new Event { Id = "first", AgentSessionId = "agent" }, terminal)).IsTrue();
            _ = await Assert.That(repository.AppendToolTerminal(new Event { Id = "replay", AgentSessionId = "agent" }, terminal)).IsFalse();
            _ = await Assert.That(repository.Replay()).Count().IsEqualTo(1);
        }

        using var reopened = SessionDatabase.Open(resources.DatabasePath);
        var restored = new EventRepository(reopened).ToolTerminals("agent").Single();
        _ = await Assert.That(restored.ToolCallId).IsEqualTo("call-1");
        _ = await Assert.That(restored.Status).IsEqualTo(status);
        _ = await Assert.That(string.Join(',', restored.ResultParts.Select(part => part.Kind)))
            .IsEqualTo(status == ToolExecutionStatus.Finished ? "Text,ImageArtifact" : "Text");
    }

    [Test]
    public async Task Checkpoint_fork_copies_complete_named_history_and_remaps_tool_settlement()
    {
        var resources = Resources("checkpoint-fork");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var history = new AgentHistoryFile(resources.AgentScratch(["agent"]), ["agent"]);
        var childHistory = new AgentHistoryFile(resources.AgentScratch(["child"]), ["child"]);
        var repository = new EventRepository(database).BindAgentHistory(history);
        var childRepository = repository.BindAgentHistory(childHistory);
        repository.AppendConversation(
            new Event { Id = "before", AgentSessionId = "agent" },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("before")],
            [],
            string.Empty);
        repository.AppendConversation(
            new Event { Id = "checkpoint-call", AgentSessionId = "agent" },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(string.Empty)],
            [new LLMToolCall("checkpoint", "set_checkpoint", "{\"title\":\"handoff\"}")],
            string.Empty);
        var checkpointAssistant = repository.Conversation("agent")[1].Sequence;
        _ = repository.RecordCheckpoint("agent", "handoff", checkpointAssistant, "checkpoint");
        _ = repository.AppendToolSettlement(
            new Event { Id = "checkpoint-result", AgentSessionId = "agent" },
            checkpointAssistant,
            new ToolExecutionTerminal(
                "checkpoint",
                "set_checkpoint",
                ToolExecutionStatus.Finished,
                [ConversationPart.TextPart("handoff")],
                "handoff"));
        repository.AppendConversation(
            new Event { Id = "between", AgentSessionId = "agent" },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("between")],
            [],
            string.Empty);
        repository.AppendConversation(
            new Event { Id = "spawn-call", AgentSessionId = "agent" },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(string.Empty)],
            [new LLMToolCall("spawn", "agent_spawn", "{}")],
            string.Empty);
        var spawnAssistant = repository.Conversation("agent")[^1].Sequence;

        childRepository.InitializeForkedAgentHistory(
            "agent",
            "child",
            new HistoryForkBoundary.BeforeToolBatch(spawnAssistant, "spawn"),
            HistoryForkSelection.Parse("handoff"));

        _ = await Assert.That(await File.ReadAllTextAsync(history.Path)).Contains("before");
        _ = await Assert.That(await File.ReadAllTextAsync(childHistory.Path)).Contains("handoff").And.Contains("between").And.DoesNotContain("before");

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
    public async Task Full_fork_after_completed_history_copies_settled_batches_and_checkpoints()
    {
        var resources = Resources("full-fork");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database);
        repository.AppendConversation(
            new Event { Id = "before", AgentSessionId = "agent" },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("before")],
            [],
            string.Empty);
        repository.AppendConversation(
            new Event { Id = "checkpoint-call", AgentSessionId = "agent" },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(string.Empty)],
            [new LLMToolCall("checkpoint", "set_checkpoint", "{\"title\":\"handoff\"}")],
            string.Empty);
        var assistant = repository.Conversation("agent")[^1].Sequence;
        _ = repository.RecordCheckpoint("agent", "handoff", assistant, "checkpoint");
        _ = repository.AppendToolSettlement(
            new Event { Id = "checkpoint-result", AgentSessionId = "agent" },
            assistant,
            new ToolExecutionTerminal(
                "checkpoint",
                "set_checkpoint",
                ToolExecutionStatus.Finished,
                [ConversationPart.TextPart("handoff")],
                "handoff"));
        repository.AppendConversation(
            new Event { Id = "after", AgentSessionId = "agent" },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("after")],
            [],
            string.Empty);

        repository.InitializeForkedAgentHistory(
            "agent",
            "child",
            new HistoryForkBoundary.AfterCompletedHistory(),
            HistoryForkSelection.Parse("full"));

        var child = repository.Conversation("child");
        _ = await Assert.That(child).Count().IsEqualTo(4);
        _ = await Assert.That(string.Join(',', child.Select(item => item.Parts.Single().Text)))
            .IsEqualTo("before,,handoff,after");
        _ = await Assert.That(repository.ToolTerminals("child")).HasSingleItem();
        _ = await Assert.That(repository.LatestUsableCheckpoint("child", "handoff", long.MaxValue))
            .IsNotNull();
    }

    [Test]
    public async Task Full_fork_after_completed_history_copies_effective_compaction_state()
    {
        var resources = Resources("full-fork-compaction");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database);
        repository.AppendConversation(
            new Event { Id = "compacted", AgentSessionId = "agent" },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("compacted away")],
            [],
            string.Empty);
        var status = new Event { Id = "status", AgentSessionId = "agent" };
        _ = await Assert.That(repository.AppendCompactionStatus(
            status,
            new CompactionSnapshot("effective summary", 1),
            "effective status")).IsTrue();
        repository.AppendConversation(
            new Event { Id = "tail", AgentSessionId = "agent" },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("retained tail")],
            [],
            string.Empty);

        repository.InitializeForkedAgentHistory(
            "agent",
            "child",
            new HistoryForkBoundary.AfterCompletedHistory(),
            HistoryForkSelection.Parse("full"));

        var context = repository.CompactionHistory("child")
            ?? throw new InvalidOperationException("Expected cloned compaction history.");
        _ = await Assert.That(context.Snapshot.Summary).IsEqualTo("effective summary");
        _ = await Assert.That(context.Snapshot.Watermark).IsEqualTo(0);
        _ = await Assert.That(context.Status?.Parts.Single().Text).IsEqualTo("effective status");
        _ = await Assert.That(string.Join(',', context.Tail.Select(item => item.Parts.Single().Text)))
            .IsEqualTo("retained tail");
        _ = await Assert.That(repository.Conversation("child").Select(item => item.Parts.Single().Text))
            .DoesNotContain("compacted away");
    }

    [Test]
    public async Task Full_fork_after_completed_history_rejects_incomplete_effective_group_without_writing_destination()
    {
        var resources = Resources("full-fork-incomplete");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database);
        repository.AppendConversation(
            new Event { Id = "incomplete", AgentSessionId = "agent" },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(string.Empty)],
            [new LLMToolCall("unfinished", "agent_spawn", "{}")],
            string.Empty);

        _ = await Assert.That(() => repository.InitializeForkedAgentHistory(
            "agent",
            "child",
            new HistoryForkBoundary.AfterCompletedHistory(),
            HistoryForkSelection.Parse("full"))).Throws<ArgumentException>();
        _ = await Assert.That(repository.Conversation("child")).IsEmpty();
    }

    [Test]
    public async Task Agent_bound_history_serializes_concurrent_updates_and_rejects_cross_agent_mutation()
    {
        var resources = Resources("concurrent-history");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database);
        var firstHistory = new AgentHistoryFile(resources.AgentScratch(["first"]), ["first"]);
        var secondHistory = new AgentHistoryFile(resources.AgentScratch(["second"]), ["second"]);
        var first = repository.BindAgentHistory(firstHistory);
        var second = repository.BindAgentHistory(secondHistory);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            var agent = index % 2 == 0 ? first : second;
            var sessionId = index % 2 == 0 ? "first" : "second";
            agent.AppendConversation(
                new Event { Id = $"message-{index}", AgentSessionId = sessionId },
                ConversationOrigin.UserInput,
                LLMRole.User,
                [ConversationPart.TextPart($"{sessionId}-{index}")],
                [],
                string.Empty);
        })));

        foreach (var (history, sessionId) in new[] { (firstHistory, "first"), (secondHistory, "second") })
        {
            var expected = string.Concat(repository.AgentHistory(sessionId).Select(entry =>
                System.Text.Json.JsonSerializer.Serialize(entry, AgentHistoryJsonContext.Default.AgentHistoryEntry) + "\n"));
            _ = await Assert.That(await File.ReadAllTextAsync(history.Path)).IsEqualTo(expected);
            _ = await Assert.That(repository.Conversation(sessionId).Count).IsEqualTo(12);
        }

        _ = await Assert.That(() => first.CleanupForkedAgentHistory("second")).Throws<InvalidOperationException>();
        _ = await Assert.That(repository.Conversation("second").Count).IsEqualTo(12);
        first.CleanupForkedAgentHistory("first");
        _ = await Assert.That(await File.ReadAllTextAsync(firstHistory.Path)).IsEmpty();
        _ = await Assert.That(await File.ReadAllTextAsync(secondHistory.Path)).Contains("second-");
    }

    [Test]
    public async Task Agent_history_projection_lists_earlier_occupants_before_the_current_agent()
    {
        var resources = Resources("occupant-history");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database);
        foreach (var sessionId in new[] { "first", "second" })
        {
            repository.AppendConversation(
                new Event { Id = $"message-{sessionId}", AgentSessionId = sessionId },
                ConversationOrigin.UserInput,
                LLMRole.User,
                [ConversationPart.TextPart($"{sessionId} occupant")],
                [],
                string.Empty);
        }

        var history = new AgentHistoryFile(resources.AgentScratch(["main", "worker"]), ["first", "second"]);
        var current = repository.BindAgentHistory(history);

        _ = await Assert.That(() => current.RefreshAgentHistory("first")).Throws<InvalidOperationException>();
        current.RefreshAgentHistory("second");
        var lines = await File.ReadAllLinesAsync(history.Path);
        _ = await Assert.That(lines.Length).IsEqualTo(2);
        _ = await Assert.That(lines[0]).Contains("first occupant");
        _ = await Assert.That(lines[1]).Contains("second occupant");
    }

    [Test]
    public async Task Agent_history_failure_recovers_committed_changes_with_the_same_file_owner()
    {
        var resources = Resources("failed-history");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database);
        var history = new AgentHistoryFile(resources.AgentScratch(["agent"]), ["agent"]);
        var agent = repository.BindAgentHistory(history);
        var historyPath = history.Path;
        var directory = Path.GetDirectoryName(historyPath) ?? throw new InvalidOperationException();
        Directory.Delete(directory, recursive: true);
        await File.WriteAllTextAsync(directory, "blocks history directory");

        agent.AppendConversation(
            new Event { Id = "message", AgentSessionId = "agent" },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("committed despite projection failure")],
            [],
            string.Empty);

        _ = await Assert.That(repository.Conversation("agent").Count).IsEqualTo(1);
        _ = await Assert.That(File.Exists(historyPath)).IsFalse();
        File.Delete(directory);
        agent.RefreshAgentHistory("agent");
        _ = await Assert.That(await File.ReadAllTextAsync(history.Path)).Contains("committed despite projection failure");
        _ = await Assert.That(Directory.GetFiles(directory, "*.tmp")).IsEmpty();
    }

    [Test]
    public async Task Agent_history_tracks_promoted_input_compaction_and_destination_fork()
    {
        var resources = Resources("projected-fork");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var repository = new EventRepository(database);
        var parentHistory = new AgentHistoryFile(resources.AgentScratch(["parent"]), ["parent"]);
        var childHistory = new AgentHistoryFile(resources.AgentScratch(["child"]), ["child"]);
        var parent = repository.BindAgentHistory(parentHistory);
        var child = repository.BindAgentHistory(childHistory);
        _ = parent.Admit("parent", "input", "promoted input", Delivery.Queue, input => new Event { Id = input.Id, AgentSessionId = "parent" });
        _ = parent.PromoteNextQueue("parent", input => new Event { Id = $"promoted-{input.Id}", AgentSessionId = "parent" });
        _ = await Assert.That(await File.ReadAllTextAsync(parentHistory.Path)).Contains("promoted input");
        _ = parent.SaveCompaction("parent", new CompactionSnapshot("saved summary", parent.Conversation("parent")[^1].Sequence));
        var parentText = await File.ReadAllTextAsync(parentHistory.Path);
        _ = await Assert.That(parentText).Contains("saved summary");

        child.InitializeForkedAgentHistory("parent", "child", new HistoryForkBoundary.AfterCompletedHistory(), HistoryForkSelection.Parse("full"));

        _ = await Assert.That(await File.ReadAllTextAsync(childHistory.Path)).Contains("saved summary");
        child.CleanupForkedAgentHistory("child");
        _ = await Assert.That(await File.ReadAllTextAsync(childHistory.Path)).IsEmpty();
        _ = await Assert.That(await File.ReadAllTextAsync(parentHistory.Path)).IsEqualTo(parentText);
    }

    [Test]
    public async Task Cleanup_forked_history_refreshes_agent_history_JSONL()
    {
        var resources = Resources("cleanup-history");
        using var database = SessionDatabase.Open(resources.DatabasePath);
        var history = new AgentHistoryFile(resources.AgentScratch(["agent"]), ["agent"]);
        var repository = new EventRepository(database, new ImageArtifactStore(resources)).BindAgentHistory(history);
        repository.AppendConversation(
            new Event { Id = "message", AgentSessionId = "agent" },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("durable")],
            [],
            string.Empty);
        var path = history.Path;
        _ = await Assert.That(await File.ReadAllTextAsync(path)).Contains("durable");

        repository.CleanupForkedAgentHistory("agent");

        _ = await Assert.That(repository.Conversation("agent")).IsEmpty();
        _ = await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(string.Empty);
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
                new Event { Id = $"{call}-assistant", AgentSessionId = "agent" },
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
                    new Event { Id = "first-result", AgentSessionId = "agent" },
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
            new HistoryForkBoundary.BeforeToolBatch(current, "latest"),
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
        _ = await Assert.That(File.Exists(contents[1].ImagePath)).IsTrue();
        _ = await Assert.That(Convert.ToBase64String(contents[1].ReadImage()))
            .IsEqualTo("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        _ = await Assert.That(contents[1].MediaType).IsEqualTo("image/png");
        _ = await Assert.That((contents[1].ImageWidth, contents[1].ImageHeight)).IsEqualTo((1, 1));
    }

    private UserSessionResources Resources(string id) => new(
        new StatePaths(_root, _root, _root),
        UserSessionId.Parse(id),
        ProjectWorkspace.FromLaunchDirectory(Directory.CreateDirectory(Path.Combine(_root, "work")).FullName));
}
