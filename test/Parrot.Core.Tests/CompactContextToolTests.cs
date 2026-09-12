using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class CompactContextToolTests : IDisposable
{
    private static readonly string[] ActiveToolCallIds = ["compact", "other"];

    private readonly List<IDisposable> _dependencies = [];
    private readonly CompactionGroupBlobStore _compactionGroupBlobs;
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-compact-context-tests", Guid.NewGuid().ToString("N"));

    public CompactContextToolTests()
    {
        _compactionGroupBlobs = new CompactionGroupBlobStore(
            new AgentScratchDirectory(Path.Combine(_workspace, "compaction-scratch")));
    }

    public void Dispose()
    {
        foreach (var dependency in _dependencies)
        {
            dependency.Dispose();
        }

        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    public async Task Tool_compacts_inline_without_deadlock_and_preserves_the_active_batch(
        CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var broker = new EventBroker();
        var provider = new QueueProvider(
        [
            LLMEvent.Completed("stop", 1, 0, 1, "first reply", []),
            LLMEvent.Completed("stop", 1, 0, 1, "second reply", []),
            LLMEvent.Completed("stop", 1, 0, 1, "third reply", []),
            LLMEvent.Completed(
                "stop",
                1,
                0,
                1,
                string.Empty,
                [
                    new LLMToolCall("compact", "compact_context", "{}"),
                    new LLMToolCall("other", "settled", "{}"),
                ]),
            LLMEvent.Completed("stop", 1, 0, 1, "summary", []),
            LLMEvent.Completed("stop", 1, 0, 1, "done", []),
        ]);
        var repository = new EventRepository(database);
        await using var session = Session(provider, repository, broker, 100_000, cancellationToken);

        foreach (var prompt in new[] { "first", "second", "third" })
        {
            _ = await session.Send(
                [ConversationPart.TextPart(prompt)],
                Identifier.MessageId(),
                Delivery.Steer,
                cancellationToken);
            await session.Settled();
        }

        _ = await session.Send(
            [ConversationPart.TextPart("compact now")],
            Identifier.MessageId(),
            Delivery.Steer,
            cancellationToken);
        await session.Settled().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        _ = await Assert.That(repository.Compaction("agent")).IsNotNull();
        _ = await Assert.That(repository.ToolTerminals("agent")).Count().IsEqualTo(2);
        var compactResult = repository.ToolTerminals("agent").Single(terminal => terminal.ToolCallId == "compact");
        _ = await Assert.That(compactResult.Message).Contains("Compacted this calling agent session.");
        var finalRequest = provider.Requests[^1];
        _ = await Assert.That(finalRequest.Messages).Contains(message =>
            message.Role == LLMRole.Assistant
            && message.ToolCalls.Select(call => call.Id).SequenceEqual(ActiveToolCallIds));
        _ = await Assert.That(finalRequest.Messages).Contains(message =>
            message.Role == LLMRole.Tool && message.ToolCallId == "compact");
        _ = await Assert.That(finalRequest.Messages).Contains(message =>
            message.Role == LLMRole.Tool && message.ToolCallId == "other");
        _ = await Assert.That(string.Join(',', repository.Replay()
            .Where(published => published.PayloadCase is Event.PayloadOneofCase.CompactionStarted
                or Event.PayloadOneofCase.StatusInjected
                or Event.PayloadOneofCase.CompactionFinished)
            .Select(published => published.PayloadCase)))
            .IsEqualTo("CompactionStarted,StatusInjected,CompactionFinished");
    }

    [Test]
    public async Task Tool_rejects_nonempty_input_and_reports_noop_or_unavailable_context(
        CancellationToken cancellationToken)
    {
        foreach (var scenario in new[]
        {
            new { Arguments = "null", ContextWindow = 100_000, Expected = "error:", Calls = 2, Compacted = false, Seed = true },
            new { Arguments = "[]", ContextWindow = 100_000, Expected = "error:", Calls = 2, Compacted = false, Seed = true },
            new { Arguments = "{\"extra\":true}", ContextWindow = 100_000, Expected = "error:", Calls = 2, Compacted = false, Seed = true },
            new { Arguments = "{\"target_context_size\":\"\"}", ContextWindow = 100_000, Expected = "error:", Calls = 2, Compacted = false, Seed = true },
            new { Arguments = "{\"target_context_size\":\"20%\"}", ContextWindow = 100_000, Expected = "Compacted this calling agent session", Calls = 3, Compacted = true, Seed = true },
            new { Arguments = "{}", ContextWindow = 100_000, Expected = "Compacted this calling agent session", Calls = 3, Compacted = true, Seed = true },
            new { Arguments = "{}", ContextWindow = 0, Expected = "Context compaction is unavailable", Calls = 2, Compacted = false, Seed = true },
        })
        {
            using var database = SessionDatabase.Open(":memory:");
            using var broker = new EventBroker();
            var responses = new List<LLMEvent>();
            if (scenario.Seed)
            {
                responses.Add(LLMEvent.Completed("stop", 1, 0, 1, "seeded history", []));
            }

            responses.Add(LLMEvent.Completed("stop", 1, 0, 1, string.Empty, [new LLMToolCall("compact", "compact_context", scenario.Arguments)]));
            responses.Add(LLMEvent.Completed("stop", 1, 0, 1, "summary", []));
            responses.Add(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
            var provider = new QueueProvider(responses);
            var repository = new EventRepository(database);
            await using var session = Session(provider, repository, broker, scenario.ContextWindow, cancellationToken);

            if (scenario.Seed)
            {
                _ = await session.Send(
                    [ConversationPart.TextPart("seed")],
                    Identifier.MessageId(),
                    Delivery.Steer,
                    cancellationToken);
                await session.Settled();
            }

            _ = await session.Send(
                [ConversationPart.TextPart("compact")],
                Identifier.MessageId(),
                Delivery.Steer,
                cancellationToken);
            await session.Settled().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            var terminal = repository.ToolTerminals("agent").Single();
            _ = await Assert.That(terminal.Message).Contains(scenario.Expected);
            _ = await Assert.That(provider.Requests).Count().IsEqualTo(scenario.Calls + (scenario.Seed ? 1 : 0));
            _ = await Assert.That(repository.Compaction("agent") is not null).IsEqualTo(scenario.Compacted);
        }
    }

    private IAgentSession Session(
        ILLMProvider provider,
        EventRepository repository,
        EventBroker broker,
        int contextWindow,
        CancellationToken cancellationToken)
    {
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id)
        {
            ContextWindow = contextWindow,
        });
        var identity = AgentIdentity.Main("agent", string.Empty, TestModels.PromptTemplates);
        var dependencies = TestModels.Dependencies(identity, broker, repository, cancellationToken);
        _dependencies.Add(dependencies);
        var factories = new IToolFactory[]
        {
            new CompactContextToolFactory(TestModels.PromptTemplates),
            new FixedToolFactory(new SettledTool("other result")),
        };
        return new AgentSession(
            identity,
            AgentSessionParentScope.Root(),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            broker,
            repository,
            factories,
            new TestToolDefinitionsFixture("compact_context", "settled").Definitions,
            TestModels.MaterializePrompt(identity, _workspace, _workspace),
            new ToolOutputBlobStore(_workspace),
            _compactionGroupBlobs,
            new Compactor(99, 30, 60_000, 1024, TestModels.PromptTemplates),
            new ProviderSessions(TestDiagnosticLog.Instance, "agent-test"),
            new ContextCadence(),
            TestModels.PromptTemplates,
            dependencies.ChildQuestions,
            dependencies.ExitReminder,
            dependencies.Profile,
            new TestCompletionCallbacksFixture(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, broker).Callbacks,
            new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security,
            dependencies.Status,
            dependencies.Queues,
            new AgentSessionActivity(TimeProvider.System),
            TestDiagnosticLog.Instance,
            cancellationToken);
    }

    private sealed class QueueProvider(IEnumerable<LLMEvent> responses) : ILLMProvider
    {
        private readonly Queue<LLMEvent> _responses = new(responses);
        private readonly List<LLMRequest> _requests = [];

        public string Id => "compact-context";

        public IReadOnlyList<LLMRequest> Requests => _requests;

        public IReadOnlyList<LLMModel> SeedModels() => [];

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LLMModel>>([]);

        public async IAsyncEnumerable<LLMEvent> Call(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(request);
            await Task.Yield();
            yield return _responses.Dequeue();
        }
    }
}
