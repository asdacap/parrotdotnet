using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class CompactorAndContextTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(), "parrot-context-tests", Guid.NewGuid().ToString("n"));

    private readonly string _workspace;
    private readonly string _configDirectory;

    public CompactorAndContextTests()
    {
        _workspace = Path.Combine(_temporaryDirectory, "workspace");
        _configDirectory = Path.Combine(_temporaryDirectory, "config");
        _ = Directory.CreateDirectory(_workspace);
        _ = Directory.CreateDirectory(_configDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task System_context_includes_platform_cwd_and_agents_files()
    {
        await File.WriteAllTextAsync(Path.Combine(_configDirectory, "AGENTS.md"), "GLOBAL RULE: be concise.");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "AGENTS.md"), "PROJECT RULE: be terse.");

        var built = new SystemContextBuilder(_workspace, _configDirectory, "2026-07-24", string.Empty).Build();

        _ = await Assert.That(built).Contains("2026-07-24");
        _ = await Assert.That(built).Contains(_workspace);
        _ = await Assert.That(built).Contains("GLOBAL RULE: be concise.");
        _ = await Assert.That(built).Contains("PROJECT RULE: be terse.");
        _ = await Assert.That(built.IndexOf("GLOBAL RULE: be concise.", StringComparison.Ordinal))
            .IsLessThan(built.IndexOf("PROJECT RULE: be terse.", StringComparison.Ordinal));
    }

    [Test]
    public async Task Model_prompt_context_sorts_configured_guidance_and_filters_disabled_aliases()
    {
        var provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var snapshot = new ModelAliasSnapshot(
        [
            new("zeta", model.Selector, "last", null),
            new("disabled", string.Empty, "hidden", null),
            new("alpha", model.Selector, "first", null),
        ]);
        var selection = new AgentTurnSelection(
            new ModelSelector(model.Selector),
            new ResolvedModelSelection(new ModelSelector(model.Selector), null, model, snapshot),
            null,
            SecurityProfile.Compose(readOnly: false, [], [], []));

        var built = new ModelPromptContext(new Dictionary<string, string>(StringComparer.Ordinal))
            .Build("epoch", selection);

        _ = await Assert.That(built).Contains($"- alpha: {model.Selector} — first");
        _ = await Assert.That(built).Contains($"- zeta: {model.Selector} — last");
        _ = await Assert.That(built).DoesNotContain("disabled");
        _ = await Assert.That(built.IndexOf("- alpha:", StringComparison.Ordinal))
            .IsLessThan(built.IndexOf("- zeta:", StringComparison.Ordinal));
    }

    [Test]
    public async Task Model_prompt_context_prefers_alias_then_exact_then_base_augmentation()
    {
        var provider = new UnusedProvider();
        var baseModel = new LLMModel("model", provider.Id);
        var variant = new Parrot.Llm.ModelVariant("high", "xhigh");
        var model = new ProviderModel(provider, baseModel, variant);
        var alias = new ModelAliasDefinition("preferred", model.Selector, "primary", "alias augmentation");
        var snapshot = new ModelAliasSnapshot([alias]);
        var prompt = new ModelPromptContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [model.Selector] = "exact augmentation",
            [$"{provider.Id}/{baseModel.Id}"] = "base augmentation",
        });
        AgentTurnSelection Selection(ModelAliasDefinition? selectedAlias) =>
            new(
                new ModelSelector(selectedAlias?.Name ?? model.Selector),
                new ResolvedModelSelection(
                    new ModelSelector(selectedAlias?.Name ?? model.Selector), selectedAlias, model, snapshot),
                null,
                SecurityProfile.Compose(readOnly: false, [], [], []));

        var aliasBuilt = prompt.Build("epoch", Selection(alias));
        var suppressed = prompt.Build("epoch", Selection(alias with { AugmentSystemPrompt = string.Empty }));
        var exactBuilt = prompt.Build("epoch", Selection(null));
        var baseOnly = new ModelPromptContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{provider.Id}/{baseModel.Id}"] = "base augmentation",
        }).Build("epoch", Selection(null));

        _ = await Assert.That(aliasBuilt).Contains("alias augmentation");
        _ = await Assert.That(aliasBuilt).DoesNotContain("exact augmentation");
        _ = await Assert.That(suppressed).DoesNotContain("augmentation");
        _ = await Assert.That(exactBuilt).Contains("exact augmentation");
        _ = await Assert.That(exactBuilt).DoesNotContain("base augmentation");
        _ = await Assert.That(baseOnly).Contains("base augmentation");
    }

    [Test]
    public async Task Agent_session_compaction_preserves_the_current_history(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var broker = new EventBroker();
        var provider = new ScriptedProvider("reply");
        var model = new ProviderModel(
            provider,
            new LLMModel("model", provider.Id),
            new Parrot.Llm.ModelVariant("high", "xhigh"));
        var session = new AgentSession(
            AgentIdentity.Main("agent", string.Empty),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            broker,
            new EventRepository(database),
            [],
            new SystemContextBuilder(_workspace, _workspace, "2026-07-24", string.Empty),
            new TodoCollection("agent", new EventRepository(database), broker),
            new ModelPromptContext(new Dictionary<string, string>(StringComparer.Ordinal)),
            new Compactor(tokenBudget: 0),
            profile: null,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            status: null,
            cancellationToken);

        _ = await session.Send(
            "keep this prompt", Identifier.MessageId(), Delivery.Steer, cancellationToken);
        _ = await session.ResultSettled();

        var inferenceRequest = provider.Requests.Single();
        _ = await Assert.That(inferenceRequest.Messages)
            .Contains(message => message.Role == LLMRole.User && message.Content == "keep this prompt");
        _ = await Assert.That(inferenceRequest.Model).IsEqualTo("model");
        _ = await Assert.That(inferenceRequest.Reasoning?.Effort).IsEqualTo("xhigh");
        _ = await Assert.That(inferenceRequest.Reasoning?.Summary).IsEqualTo("auto");
    }

    [Test]
    public async Task Compaction_shrinks_history_and_keeps_the_recent_tail(CancellationToken cancellationToken)
    {
        var provider = new ScriptedProvider("SUMMARY OF EARLIER");

        // A budget of zero forces compaction; the four newest messages survive.
        var compactor = new Compactor(tokenBudget: 0);

        var history = new List<LLMMessage>();

        for (var index = 0; index < 20; index++)
        {
            history.Add(LLMMessage.User($"message {index}"));
        }

        _ = await Assert.That(compactor.ShouldCompact(history)).IsTrue();

        var compacted = await Compactor.Compact(provider, "model", history, cancellationToken);

        _ = await Assert.That(compacted.Count).IsLessThan(history.Count);
        _ = await Assert.That(compacted[0].Role).IsEqualTo(LLMRole.System);
        _ = await Assert.That(compacted[0].Content).Contains("SUMMARY OF EARLIER");

        // The tail is kept verbatim so the thread is not lost.
        _ = await Assert.That(compacted[^1].Content).IsEqualTo("message 19");

        history =
        [
            .. Enumerable.Range(0, 4).Select(index => LLMMessage.User($"old {index}")),
            LLMMessage.Assistant(
                string.Empty,
                [
                    new LLMToolCall("call-1", "read", "{}"),
                    new LLMToolCall("call-2", "read", "{}"),
                ]),
            LLMMessage.ToolResult("call-1", "first result"),
            LLMMessage.ToolResult("call-2", "second result"),
            LLMMessage.User("latest"),
        ];

        compacted = await Compactor.Compact(provider, "model", history, cancellationToken);

        _ = await Assert.That(compacted[1].ToolCalls).Count().IsEqualTo(2);
        _ = await Assert.That(compacted[1].ToolCalls[0].Id).IsEqualTo("call-1");
        _ = await Assert.That(compacted[2].ToolCallId).IsEqualTo("call-1");
        _ = await Assert.That(compacted[3].ToolCallId).IsEqualTo("call-2");
    }
}
