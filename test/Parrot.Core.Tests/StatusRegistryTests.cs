using System.Globalization;
using System.Runtime.Versioning;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Security;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class StatusRegistryTests
{
    [Test]
    public async Task Observe_composes_available_status_by_key_and_resamples_providers()
    {
        var query = new StatusQuery("session", string.Empty, string.Empty, "plan", "openai/gpt/high");
        var calls = 0;
        StatusQuery? observedQuery = null;
        var registry = new StatusRegistry(
            new ScriptedStatusProvider(
                "runtime:selection",
                (observed, _) =>
                {
                    observedQuery = observed;
                    calls++;
                    return ValueTask.FromResult(StatusObservation.AvailableText($"selection {calls}"));
                }),
            new ScriptedStatusProvider(
                "runtime:unavailable",
                static (_, _) => ValueTask.FromResult(StatusObservation.Unavailable)),
            new ScriptedStatusProvider(
                "runtime:blank",
                static (_, _) => ValueTask.FromResult(StatusObservation.AvailableText("  "))));
        IStatusProvider profile = new ProfileStatusProvider("profile:plan", "profile prompt");

        var first = await registry.Observe(query, profile, CancellationToken.None);
        var second = await registry.Observe(query, profile, CancellationToken.None);

        _ = await Assert.That(first).IsEqualTo("profile prompt\n\nselection 1");
        _ = await Assert.That(second).IsEqualTo("profile prompt\n\nselection 2");
        _ = await Assert.That(observedQuery).IsEqualTo(query);
    }

    [Test]
    [Arguments(123, 1000, 12, true, "123", "1k")]
    [Arguments(1500, 1000, 100, true, "1.5k", "1k")]
    [Arguments(62539, 1050000, 5, true, "62.5k", "1.1M")]
    [Arguments(123, 0, 0, false, "123", "0")]
    [Arguments(123, -1, 0, false, "123", "-1")]
    public async Task Context_reports_available_and_unavailable_windows_exactly(long estimatedTokens, int contextLimit, int usage, bool available, string formattedTokens, string formattedLimit)
    {
        IStatusProvider provider = new ContextStatusProvider(
                new ContextSnapshot(estimatedTokens, contextLimit, available ? usage : null, 90),
                TestModels.PromptTemplates);
        var observation = await provider.Observe(new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model"), CancellationToken.None);

        var expectedUsage = available ? FormattableString.Invariant($"{usage}% used") : "unavailable";
        _ = await Assert.That(observation.Available).IsTrue();
        _ = await Assert.That(observation.Text).IsEqualTo(FormattableString.Invariant(
            $"Context: {expectedUsage} ({formattedTokens} estimated tokens / {formattedLimit} limit); reminders every 10%; automatic compaction at 90%."));
    }

    [Test]
    [Arguments(100_000L, "100000")]
    [Arguments(null, "unavailable")]
    public async Task Context_reports_exact_override_without_replacing_model_window(long? triggerTokens, string expectedTrigger)
    {
        var snapshot = new ContextSnapshot(100_000, 1_000_000, 10, 10)
        {
            InputLimit = 900_000,
            TriggerTokens = triggerTokens,
            HasContextLimitOverride = true,
        };
        IStatusProvider provider = new ContextStatusProvider(snapshot, TestModels.PromptTemplates);
        var observation = await provider.Observe(
            new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model"),
            CancellationToken.None);

        _ = await Assert.That(observation.Text).Contains("1M model context window")
            .And.Contains($"strictly above {expectedTrigger} tokens");
        _ = await Assert.That(snapshot.ExceedsTrigger).IsFalse();
        _ = await Assert.That(snapshot.ExceedsCompactionTrigger(100_001)).IsEqualTo(triggerTokens is not null);
        _ = await Assert.That(snapshot.ExceedsInputLimit).IsFalse();
    }

    [Test]
    [Arguments(true, false, "Context: -12% used (-123 estimated tokens / 1k limit); reminders every 10%; automatic compaction at -90%.")]
    [Arguments(false, false, "Context: unavailable (-123 estimated tokens / -1 limit); reminders every 10%; automatic compaction at -90%.")]
    [Arguments(true, true, "-12|-123|1k|10|-90")]
    [Arguments(false, true, "missing|-123|-1|10|-90")]
    public async Task Context_preserves_invariant_metrics_and_supports_structured_overrides(bool available, bool custom, string expected)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "minus";
        CultureInfo.CurrentCulture = culture;
        try
        {
            var templates = custom
                ? new PromptTemplateCatalog(new Dictionary<string, PromptTemplate>(StringComparer.Ordinal)
                {
                    ["status.context"] = new(
                        new HashSet<string>(["available", "usage", "estimated_tokens", "context_limit", "cadence", "trigger"], StringComparer.Ordinal),
                        new HashSet<string>(["available", "estimated_tokens", "context_limit", "cadence", "trigger"], StringComparer.Ordinal),
                        new ScribanPromptTemplateEngine("prompt_templates.status.context", "{{ if available }}{{ usage }}{{ else }}missing{{ end }}|{{ estimated_tokens }}|{{ context_limit }}|{{ cadence }}|{{ trigger }}")),
                })
                : TestModels.PromptTemplates;
            IStatusProvider provider = new ContextStatusProvider(new ContextSnapshot(-123, available ? 1000 : -1, available ? -12 : null, -90), templates);

            var observation = await provider.Observe(new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model"), CancellationToken.None);

            _ = await Assert.That(observation.Text).IsEqualTo(expected);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    [Arguments("", "ignored", "build|{{ model }}|root")]
    [Arguments("parent", " \t", "build|{{ model }}|parent")]
    [Arguments("parent", " name ", "build|{{ model }}|parent/ name ")]
    public async Task Selection_supports_structured_overrides(string parentSessionId, string parentSessionName, string expected)
    {
        var arguments = new HashSet<string>(["profile", "model", "parent_session_id", "parent_session_name", "has_parent", "has_parent_name"], StringComparer.Ordinal);
        var templates = new PromptTemplateCatalog(new Dictionary<string, PromptTemplate>(StringComparer.Ordinal)
        {
            ["status.selection"] = new(arguments, arguments, new ScribanPromptTemplateEngine(
                "prompt_templates.status.selection",
                "{{ profile }}|{{ model }}|{{ if has_parent }}{{ parent_session_id }}{{ if has_parent_name }}/{{ parent_session_name }}{{ end }}{{ else }}root{{ end }}")),
        });
        IStatusProvider provider = new SelectionStatusProvider(templates);

        var observation = await provider.Observe(new StatusQuery("child", parentSessionId, parentSessionName, "build", "{{ model }}"), CancellationToken.None);

        _ = await Assert.That(observation.Text).IsEqualTo(expected);
    }

    [Test]
    public async Task Additional_context_provider_preserves_order_and_runtime_exclusions()
    {
        await using var fixture = new RuntimeTreeFixture();
        _ = fixture.Build(AgentIdentity.Main("session", "main", TestModels.PromptTemplates), AgentSessionParentLink.Root());
        IStatusProvider runtime = new RuntimeTreeStatusProvider(fixture.Registry, TestModels.PromptTemplates);
        var full = new StatusRegistry(new GeneratedTimeStatusProvider(TimeProvider.System, TestModels.PromptTemplates), new SelectionStatusProvider(TestModels.PromptTemplates), runtime);
        IStatusProvider context = new ContextStatusProvider(new ContextSnapshot(123, 1000, 12, 90), TestModels.PromptTemplates);
        var query = new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model");

        var fullText = await full.ObserveWithProvider(query, new ProfileStatusProvider("profile:build", "profile prompt"), context, CancellationToken.None);
        var runtimeText = await new StatusRegistry(runtime).ObserveWithProvider(query, null, context, CancellationToken.None);

        _ = await Assert.That(fullText).StartsWith("profile prompt\n\nGenerated at: ");
        _ = await Assert.That(fullText).Contains("\n\nRuntime:\n- agent: main\n\nContext: 12% used");
        _ = await Assert.That(fullText).EndsWith("\n\nActive profile: build\nModel: provider/model");
        _ = await Assert.That(runtimeText).Contains("Context: 12% used");
        _ = await Assert.That(runtimeText).DoesNotContain("profile prompt");
        _ = await Assert.That(runtimeText).DoesNotContain("Active profile:");
        _ = await Assert.That(runtimeText).DoesNotContain("Model:");
    }

    [Test]
    public async Task Additional_context_snapshots_are_isolated_per_observation()
    {
        var registry = new StatusRegistry(new ScriptedStatusProvider("runtime:value", static (_, _) => ValueTask.FromResult(StatusObservation.AvailableText("shared"))));
        var query = new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model");
        IStatusProvider first = new ContextStatusProvider(new ContextSnapshot(100, 1000, 10, 90), TestModels.PromptTemplates);
        IStatusProvider second = new ContextStatusProvider(new ContextSnapshot(800, 1000, 80, 90), TestModels.PromptTemplates);
        var observations = await Task.WhenAll(
            registry.ObserveWithProvider(query, null, first, CancellationToken.None),
            registry.ObserveWithProvider(query, null, second, CancellationToken.None));

        _ = await Assert.That(observations[0]).Contains("10% used");
        _ = await Assert.That(observations[0]).DoesNotContain("80% used");
        _ = await Assert.That(observations[1]).Contains("80% used");
        _ = await Assert.That(observations[1]).DoesNotContain("10% used");
    }

    [Test]
    [Arguments("profile prompt", true, "profile prompt")]
    [Arguments("  ", false, "")]
    public async Task Profile_reports_only_a_nonblank_prompt(string prompt, bool available, string expected)
    {
        IStatusProvider provider = new ProfileStatusProvider("profile:build", prompt);
        var observation = await provider.Observe(
            new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model"),
            CancellationToken.None);

        _ = await Assert.That(observation.Available).IsEqualTo(available);
        _ = await Assert.That(observation.Text).IsEqualTo(expected);
    }

    [Test]
    public async Task Generated_time_reports_the_current_utc_time()
    {
        var timeProvider = new ControlledTimeProvider();
        timeProvider.Advance(TimeSpan.FromDays(1));

        IStatusProvider provider = new GeneratedTimeStatusProvider(timeProvider, TestModels.PromptTemplates);
        var observation = await provider.Observe(
            new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model"),
            CancellationToken.None);

        _ = await Assert.That(observation.Text).IsEqualTo("Generated at: 1970-01-02T00:00:00.0000000+00:00");
    }

    [Test]
    public async Task Selection_reports_a_complete_canonical_requested_selector()
    {
        IStatusProvider provider = new SelectionStatusProvider(TestModels.PromptTemplates);
        var observation = await provider.Observe(
            new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model/medium"),
            CancellationToken.None);

        _ = await Assert.That(observation.Text)
            .IsEqualTo("Active profile: build\nModel: provider/model/medium");
        _ = await Assert.That(observation.Text).DoesNotContain("Variant:");
    }

    [Test]
    [Arguments("", "")]
    [Arguments(" \t\n", "ignored-name")]
    [Arguments("parent", "")]
    [Arguments("parent", "  ")]
    [Arguments("parent", "\t\n")]
    [Arguments("parent", "main-agent")]
    [Arguments(" parent ", " main-agent ")]
    [Arguments("{{ hostile }}", "{name}\n{{ name }}")]
    public async Task Selection_reports_parent_context_when_present(string parentSessionId, string parentSessionName)
    {
        IStatusProvider provider = new SelectionStatusProvider(TestModels.PromptTemplates);
        var observation = await provider.Observe(
            new StatusQuery("child", parentSessionId, parentSessionName, "build", "low_llm"),
            CancellationToken.None);

        var parent = string.IsNullOrWhiteSpace(parentSessionId)
            ? string.Empty
            : string.IsNullOrWhiteSpace(parentSessionName)
                ? $"\nParent session: {parentSessionId}"
                : $"\nParent session: {parentSessionId} ({parentSessionName})";
        _ = await Assert.That(observation.Text).IsEqualTo($"Active profile: build\nModel: low_llm{parent}");
    }

    [Test]
    [Arguments("missing-namespace")]
    [Arguments(":missing-name")]
    [Arguments("missing-namespace:")]
    [Arguments("runtime:bad key")]
    [Arguments("runtime:bad\tkey")]
    [Arguments("runtime:bad\nkey")]
    public async Task Register_rejects_unstable_keys(string key)
    {
        var registry = new StatusRegistry();
        IStatusProvider provider = new ScriptedStatusProvider(key, static (_, _) => ValueTask.FromResult(StatusObservation.AvailableText("status")));

        _ = await Assert.That(() => registry.Register(provider)).Throws<StatusRegistryException>();
    }

    [Test]
    public async Task Runtime_tree_nests_queues_processes_and_active_agents(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var fixture = new RuntimeTreeFixture();
        fixture.EnableProcesses();
        var root = fixture.Build(AgentIdentity.Main("root", "main", TestModels.PromptTemplates), AgentSessionParentLink.Root());
        var child = fixture.Build(AgentIdentity.Child("child", "root", "main", "worker", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates), AgentSessionParentLink.Child(root, AgentCompletionDeliveryPolicy.RetainedOnly));
        _ = await child.Session.SendTextMessage("work", cancellationToken);
        await fixture.Provider.Arrived(cancellationToken);
        _ = root.Queues.Create("work", "queued work\n{{ hostile }}");
        _ = child.Queues.Create("results", string.Empty);
        _ = child.Processes.StartPipe("fetch", "sleep 30", "call", ProcessEnvironmentOverrides.Empty, child.Session, SecurityProfile.Compose(readOnly: false, [], [], []));
        _ = root.Processes.StartPipe("build", "sleep 30", "call", ProcessEnvironmentOverrides.Empty, root.Session, SecurityProfile.Compose(readOnly: false, [], [], []));
        IStatusProvider provider = new RuntimeTreeStatusProvider(fixture.Registry, TestModels.PromptTemplates);

        var observation = await provider.Observe(
            new StatusQuery("root", string.Empty, string.Empty, "build", "provider/model"),
            cancellationToken);

        _ = await Assert.That(observation.Text).IsEqualTo(
            """
            Runtime:
            - agent: main
              - queue: work (0 items, description: "queued work\n{{ hostile }}")
              - process: root/build (shell, running, name: build)
              - agent: worker (running)
                - queue: results (0 items)
                - process: child/fetch (shell, running, name: fetch)
            """);
    }

    [Test]
    [Arguments(false, "Runtime:\n- agent: main")]
    [Arguments(true, "tree:main;")]
    public async Task Runtime_tree_reports_only_the_root_agent_when_idle(bool custom, string expected, CancellationToken cancellationToken)
    {
        await using var fixture = new RuntimeTreeFixture();
        _ = fixture.Build(AgentIdentity.Main("root", "main", TestModels.PromptTemplates), AgentSessionParentLink.Root());
        var templates = custom
            ? new PromptTemplateCatalog(new Dictionary<string, PromptTemplate>(StringComparer.Ordinal)
            {
                ["status.runtime"] = new(
                    new HashSet<string>(["section", "agents", "runs"], StringComparer.Ordinal),
                    new HashSet<string>(["section", "agents", "runs"], StringComparer.Ordinal),
                    new ScribanPromptTemplateEngine("prompt_templates.status.runtime", "{{ section }}:{{ for agent in agents }}{{ agent.name }};{{ end }}")),
            })
            : TestModels.PromptTemplates;
        IStatusProvider provider = new RuntimeTreeStatusProvider(fixture.Registry, templates);

        var observation = await provider.Observe(
            new StatusQuery("root", string.Empty, string.Empty, "build", "provider/model"),
            cancellationToken);

        _ = await Assert.That(provider.Key).IsEqualTo("runtime:queues");
        _ = await Assert.That(observation.Available).IsTrue();
        _ = await Assert.That(observation.Text).IsEqualTo(expected);
    }

    [Test]
    public async Task Register_and_profile_reject_duplicate_keys()
    {
        var registry = new StatusRegistry(new ScriptedStatusProvider("runtime:selection", static (_, _) => ValueTask.FromResult(StatusObservation.AvailableText("first"))));

        _ = await Assert.That(() => registry.Register(new ScriptedStatusProvider("runtime:selection", static (_, _) => ValueTask.FromResult(StatusObservation.AvailableText("second")))))
            .Throws<StatusRegistryException>();
        _ = await Assert.That(async () => await registry.Observe(
                new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model"),
                new ScriptedStatusProvider("runtime:selection", static (_, _) => ValueTask.FromResult(StatusObservation.AvailableText("profile"))),
                CancellationToken.None))
            .Throws<StatusRegistryException>();
    }

    [Test]
    public async Task Observe_wraps_provider_failures_with_the_provider_key()
    {
        var registry = new StatusRegistry(new ScriptedStatusProvider(
            "runtime:failure",
            static (_, _) => ValueTask.FromException<StatusObservation>(new InvalidOperationException("failed"))));

        var exception = await Assert.That(async () => await registry.Observe(
                new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model"),
                null,
                CancellationToken.None))
            .Throws<StatusRegistryException>();

        _ = await Assert.That(exception?.Message).Contains("runtime:failure");
        _ = await Assert.That(exception?.InnerException).IsTypeOf<InvalidOperationException>();
    }

    private sealed class RuntimeTreeFixture : IAsyncDisposable
    {
        private readonly string _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "parrot-status-tests", Guid.NewGuid().ToString("n"))).FullName;

        private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
        private readonly IEventBroker _broker = new EventBroker();
        private readonly IEventRepository _repository;
        private readonly IModelRouter _router;
        private readonly UserSessionResources _resources;
        private ProcessRunner _runner = new(string.Empty);

        public RuntimeTreeFixture()
        {
            _repository = new EventRepository(_database);
            _router = TestModels.Route(new ProviderModel(Provider, new LLMModel("model", Provider.Id)));
            _resources = new UserSessionResources(new StatePaths(_root, _root, _root), UserSessionId.Parse(Guid.NewGuid().ToString("n")), ProjectWorkspace.FromLaunchDirectory(_root));
            Registry = TestModels.Registry(new AgentTaskTestSessionFactory(_router), _broker, _repository, new TestProfileFixture().Registry, TestModels.PromptTemplates, CancellationToken.None);
        }

        public SteppedProvider Provider { get; } = new(LLMEvent.Completed("stop", 1, 0, 1, "done", []));

        public IAgentRegistry Registry { get; }

        public IAgentSessionScope Build(AgentIdentity identity, AgentSessionParentLink parentLink)
        {
            var scope = TestAgentSessionScope.BuildWithResources(
                identity,
                parentLink,
                Registry,
                TestModels.PromptTemplates,
                _resources,
                _runner,
                TestDiagnosticLog.Instance,
                (parentScope, owningScope, children, childQuestions) =>
                {
                    var exitReminder = new ExitReminder(_repository, TestModels.PromptTemplates, identity.SessionId);
                    var mode = new TestProfileFixture().Mode;
                    return new AgentSession(identity, parentScope, _router.Resolve(string.Empty).RequestedSelector, _router, _broker, _repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, _root, _root), new ToolOutputBlobStore(_root), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "status-test", null), new ContextCadence(), TestModels.PromptTemplates, childQuestions, exitReminder, mode, new TestCompletionCallbacksFixture(childQuestions, new ActiveWorkCompletionReminder(children, owningScope.Processes, TestModels.PromptTemplates, null), exitReminder, _repository, _broker).Callbacks, new SecurityProfileTestFixture(mode.Profile.SecurityProfile).Security, Registry.RequireStatus(), owningScope.Queues, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, CancellationToken.None);
                },
                CancellationToken.None);
            if (parentLink.Parent is { } parent)
            {
                _ = parent.ChildRegistry.TryAdd(scope);
            }
            else
            {
                Registry.RegisterRootScope(scope);
            }

            return scope;
        }

        [SupportedOSPlatform("linux")]
        public void EnableProcesses()
        {
            var path = Path.Combine(_root, "sandbox");
            var script = "#!/bin/sh\nwhile [ \"$1\" != \"--\" ]; do\n"
                + "  if [ \"$1\" = \"--chdir\" ]; then shift; cd \"$1\" || exit; "
                + "elif [ \"$1\" = \"--setenv\" ]; then export \"$2=$3\"; shift 2; fi\n"
                + "  shift\ndone\nshift\nexec \"$@\"\n";
            File.WriteAllText(path, script);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _runner = new ProcessRunner(path);
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync().ConfigureAwait(false);
            Provider.Dispose();
            _broker.Dispose();
            _database.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ScriptedStatusProvider(
        string key,
        Func<StatusQuery, CancellationToken, ValueTask<StatusObservation>> observe) : IStatusProvider
    {
        public string Key { get; } = key;

        public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken) =>
            observe(query, cancellationToken);
    }
}
