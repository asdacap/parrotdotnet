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
