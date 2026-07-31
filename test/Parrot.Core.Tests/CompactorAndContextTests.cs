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

        var prompt = ComposeSystemContextProvider().Materialize(AgentIdentity.Main("session", string.Empty));
        prompt.RenewEpoch();
        var built = prompt.Build(Selection());

        _ = await Assert.That(built).StartsWith("Configured base prompt.");
        _ = await Assert.That(built).Contains("Date: 2026-07-24\n\nPlatform:");
        _ = await Assert.That(built).Contains($"\n\nWorking directory: {_workspace}");
        _ = await Assert.That(built).Contains("GLOBAL RULE: be concise.");
        _ = await Assert.That(built).Contains("PROJECT RULE: be terse.");
        _ = await Assert.That(built).Contains("Available CLI utilities: none");
        _ = await Assert.That(built).Contains("Available optional CLI utilities: none");
        var projectIndex = built.IndexOf("PROJECT RULE: be terse.", StringComparison.Ordinal);
        var expectedIndex = built.IndexOf("Available CLI utilities: none", StringComparison.Ordinal);
        var dateIndex = built.IndexOf("Date: 2026-07-24", StringComparison.Ordinal);
        var platformIndex = built.IndexOf("Platform:", StringComparison.Ordinal);
        var workingDirectoryIndex = built.IndexOf("Working directory:", StringComparison.Ordinal);
        var optionalIndex = built.IndexOf("Available optional CLI utilities: none", StringComparison.Ordinal);
        var subagentsIndex = built.IndexOf("Available subagents;", StringComparison.Ordinal);
        _ = await Assert.That(built.IndexOf("GLOBAL RULE: be concise.", StringComparison.Ordinal))
            .IsLessThan(projectIndex);
        _ = await Assert.That(projectIndex).IsLessThan(expectedIndex);
        _ = await Assert.That(expectedIndex).IsLessThan(dateIndex);
        _ = await Assert.That(dateIndex).IsLessThan(platformIndex);
        _ = await Assert.That(platformIndex).IsLessThan(workingDirectoryIndex);
        _ = await Assert.That(workingDirectoryIndex).IsLessThan(optionalIndex);
        _ = await Assert.That(optionalIndex).IsLessThan(subagentsIndex);
    }

    [Test]
    public async Task Session_identity_provider_omits_main_identity_and_renders_child_identity_before_subagents()
    {
        var main = ComposeSystemContextProvider().Materialize(AgentIdentity.Main("main", string.Empty));
        var child = ComposeSystemContextProvider().Materialize(
            AgentIdentity.Child("child", "main", "main-agent", "worker", 1));
        main.RenewEpoch();
        child.RenewEpoch();

        var mainBuilt = main.Build(Selection());
        var childBuilt = child.Build(Selection());

        _ = await Assert.That(mainBuilt).DoesNotContain("Child agent session:");
        _ = await Assert.That(childBuilt).Contains(
            "Child agent session: child\n"
            + "Parent agent session: main\n"
            + "Parent agent name: main-agent\n"
            + "Child agent name: worker\n"
            + "Child agent depth: 1");
        _ = await Assert.That(childBuilt.IndexOf("Child agent session:", StringComparison.Ordinal))
            .IsLessThan(childBuilt.IndexOf("Available subagents;", StringComparison.Ordinal));
    }

    [Test]
    public async Task Model_prompt_context_sorts_configured_guidance_and_filters_disabled_aliases()
    {
        var provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var snapshot = new ModelAliasSnapshot(
        [
            new("zeta", model.Selector, "last", null, null),
            new("disabled", string.Empty, "hidden", null, null),
            new("alpha", model.Selector, "first", null, null),
        ]);
        var selection = new AgentTurnSelection(
            new ModelSelector(model.Selector),
            new ResolvedModelSelection(new ModelSelector(model.Selector), null, model, snapshot),
            null,
            SecurityProfile.Compose(readOnly: false, [], [], []));

        var built = new ModelPromptProvider(new Dictionary<string, string>(StringComparer.Ordinal))
            .Materialize(AgentIdentity.Main("session", string.Empty))
            .Build(selection);

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
        var alias = new ModelAliasDefinition("preferred", model.Selector, "primary", "alias augmentation", null);
        var snapshot = new ModelAliasSnapshot([alias]);
        var prompt = new ModelPromptProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [model.Selector] = "exact augmentation",
            [$"{provider.Id}/{baseModel.Id}"] = "base augmentation",
        }).Materialize(AgentIdentity.Main("session", string.Empty));
        AgentTurnSelection Selection(ModelAliasDefinition? selectedAlias) =>
            new(
                new ModelSelector(selectedAlias?.Name ?? model.Selector),
                new ResolvedModelSelection(
                    new ModelSelector(selectedAlias?.Name ?? model.Selector), selectedAlias, model, snapshot),
                null,
                SecurityProfile.Compose(readOnly: false, [], [], []));

        var aliasBuilt = prompt.Build(Selection(alias));
        var suppressed = prompt.Build(Selection(alias with { AugmentSystemPrompt = string.Empty }));
        var exactBuilt = prompt.Build(Selection(null));
        var baseOnly = new ModelPromptProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{provider.Id}/{baseModel.Id}"] = "base augmentation",
        }).Materialize(AgentIdentity.Main("session", string.Empty)).Build(Selection(null));

        _ = await Assert.That(aliasBuilt).Contains("alias augmentation");
        _ = await Assert.That(aliasBuilt).DoesNotContain("exact augmentation");
        _ = await Assert.That(suppressed).DoesNotContain("augmentation");
        _ = await Assert.That(exactBuilt).Contains("exact augmentation");
        _ = await Assert.That(exactBuilt).DoesNotContain("base augmentation");
        _ = await Assert.That(baseOnly).Contains("base augmentation");
    }

    [Test]
    public async Task Model_prompt_context_includes_the_selected_profile_prompt()
    {
        var built = new ModelPromptProvider(new Dictionary<string, string>(StringComparer.Ordinal))
            .Materialize(AgentIdentity.Main("session", string.Empty))
            .Build(Selection(Profile(null, new HashSet<string>(StringComparer.Ordinal))));

        _ = await Assert.That(built).Contains("Test prompt");
        _ = await Assert.That(built).DoesNotContain("Hard rules:");
    }

    [Test]
    public async Task Queue_guidance_is_omitted_when_queue_creation_is_globally_disabled()
    {
        var prompt = new QueueGuidancePrompt();
        var enabled = prompt.Build(Selection(Profile(
            allowedTools: null,
            new HashSet<string>(StringComparer.Ordinal))));
        var disallowed = prompt.Build(Selection(Profile(
            ["read"],
            new HashSet<string>(StringComparer.Ordinal))));
        var disabled = prompt.Build(Selection(Profile(
            ["queue_create"],
            new HashSet<string>(["queue_create"], StringComparer.Ordinal))));

        _ = await Assert.That(enabled).Contains("use queue");
        _ = await Assert.That(disallowed).IsEmpty();
        _ = await Assert.That(disabled).IsEmpty();
    }

    [Test]
    public async Task Composite_system_prompt_validates_orders_and_materializes_per_session()
    {
        var first = new PromptTestProvider("test:z", "z");
        var second = new PromptTestProvider("test:a", "a");
        var composite = new CompositeSystemPromptProvider("test:composite", [first, second]);

        var main = composite.Materialize(AgentIdentity.Main("main", string.Empty));
        var child = composite.Materialize(AgentIdentity.Child("child", "main", "main-agent", "worker", 1));
        main.RenewEpoch();
        child.RenewEpoch();

        _ = await Assert.That(main.Build(Selection())).IsEqualTo("a\n\nz");
        _ = await Assert.That(child.Build(Selection())).IsEqualTo("a\n\nz");
        _ = await Assert.That(first.Materializations).IsEqualTo(2);
        _ = await Assert.That(second.Materializations).IsEqualTo(2);
        _ = await Assert.That(() => new CompositeSystemPromptProvider("test:composite", [first, first]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Agents_prompt_is_stable_until_the_epoch_is_renewed()
    {
        var agents = Path.Combine(_workspace, "AGENTS.md");
        await File.WriteAllTextAsync(agents, "first");
        var prompt = new AgentsPromptProvider(_workspace, _configDirectory)
            .Materialize(AgentIdentity.Main("session", string.Empty));

        prompt.RenewEpoch();
        var first = prompt.Build(Selection());
        await File.WriteAllTextAsync(agents, "second");
        var sameEpoch = prompt.Build(Selection());
        prompt.RenewEpoch();
        var nextEpoch = prompt.Build(Selection());

        _ = await Assert.That(first).Contains("first");
        _ = await Assert.That(sameEpoch).Contains("first");
        _ = await Assert.That(nextEpoch).Contains("second");
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
            TestModels.PromptProvider(_workspace, _workspace),
            new TodoCollection("agent", new EventRepository(database), broker),
            new ToolOutputBlobStore(_workspace),
            new Compactor(tokenBudget: 0),
            profile: null,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            status: null,
            registry: null,
            owner: null,
            cancellationToken);

        _ = await session.Send(
            "keep this prompt", Identifier.MessageId(), Delivery.Steer, cancellationToken);
        _ = await session.ResultSettled();

        var inferenceRequest = provider.Requests.Single();
        _ = await Assert.That(inferenceRequest.Instructions).Contains("2026-07-24");
        _ = await Assert.That(inferenceRequest.Messages)
            .DoesNotContain(message => message.Role == LLMRole.System);
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

    private static Parrot.Process.CliUtilityAvailability EmptyCliUtilities() =>
        Parrot.Process.CliUtilityAvailability.Inspect(
            new Parrot.Config.CliUtilityCandidates([], []),
            new Parrot.Process.ExecutableLocator(string.Empty, string.Empty));

    private static AgentTurnSelection Selection() => Selection(null);

    private static AgentTurnSelection Selection(IAgentProfile? profile)
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
            profile,
            SecurityProfile.Compose(readOnly: false, [], [], []));
    }

    private static AgentProfile Profile(
        IReadOnlyList<string>? allowedTools,
        IReadOnlySet<string> disabledTools) => new(
            "test",
            new Parrot.Config.ProfileConfig(
                "Test prompt", "Test profile.", allowedTools, 2, 3, false, []),
            [],
            disabledTools);

    private CompositeSystemPromptProvider ComposeSystemContextProvider() =>
        new(
            "test:system-context",
            [
                new BasePromptProvider("Configured base prompt."),
                new AgentsPromptProvider(_workspace, _configDirectory),
                new ExpectedCliUtilitiesProvider(EmptyCliUtilities()),
                new DateProvider("2026-07-24"),
                new PlatformProvider(),
                new WorkingDirectoryProvider(_workspace),
                new OptionalCliUtilitiesProvider(EmptyCliUtilities()),
                new SessionIdentityProvider(),
                new SubagentsProvider(TestModels.ProfileRegistry()),
            ]);

    private sealed class PromptTestProvider(string key, string text) : ISystemPromptProvider
    {
        public int Materializations { get; private set; }

        public string Key => key;

        public ISystemPrompt Materialize(AgentIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);
            Materializations++;
            return new PromptTestProduct(text);
        }
    }

    private sealed class PromptTestProduct(string text) : ISystemPrompt
    {
        private bool _renewed;

        public void RenewEpoch() => _renewed = true;

        public string Build(AgentTurnSelection selection)
        {
            ArgumentNullException.ThrowIfNull(selection);
            return _renewed ? text : throw new InvalidOperationException("Prompt was not renewed.");
        }
    }
}
