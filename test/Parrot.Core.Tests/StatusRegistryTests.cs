using Parrot.Agent;
using Parrot.Context;
using Parrot.Process;
using Parrot.Queues;
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
    [Arguments(123, 1000, 12, true)]
    [Arguments(1500, 1000, 100, true)]
    [Arguments(123, 0, 0, false)]
    [Arguments(123, -1, 0, false)]
    public async Task Context_reports_available_and_unavailable_windows_exactly(long estimatedTokens, int contextLimit, int usage, bool available)
    {
        IStatusProvider provider = new ContextStatusProvider(
                new ContextSnapshot(estimatedTokens, contextLimit, available ? usage : null, 90),
                TestModels.PromptTemplates);
        var observation = await provider.Observe(new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model"), CancellationToken.None);

        _ = await Assert.That(observation.Available).IsTrue();
        _ = await Assert.That(observation.Text).Contains($"{estimatedTokens} estimated");
        _ = await Assert.That(observation.Text).Contains($"{contextLimit} limit");
        _ = await Assert.That(observation.Text).Contains("every 10%");
        _ = await Assert.That(observation.Text).Contains("automatic compaction at 90%");
        if (available)
        {
            _ = await Assert.That(observation.Text).IsEqualTo($"Context: {usage}% used ({estimatedTokens} estimated tokens / {contextLimit} limit); reminders every 10%; automatic compaction at 90%.");
        }
        else
        {
            _ = await Assert.That(observation.Text).Contains("Context: unavailable");
            _ = await Assert.That(observation.Text).DoesNotContain("% used");
        }
    }

    [Test]
    public async Task Additional_context_provider_preserves_order_and_runtime_exclusions()
    {
        using var catalog = QueueCatalog("status-order");
        using var root = catalog.Register(AgentIdentity.Main("session", "main", TestModels.PromptTemplates));
        IStatusProvider runtime = new RuntimeTreeStatusProvider(catalog, new ProcessStatusSource(), new AgentStatusSource(), TestModels.PromptTemplates);
        var full = new StatusRegistry(new GeneratedTimeStatusProvider(TimeProvider.System, TestModels.PromptTemplates), new SelectionStatusProvider(TestModels.PromptTemplates), runtime);
        IStatusProvider context = new ContextStatusProvider(new ContextSnapshot(123, 1000, 12, 90), TestModels.PromptTemplates);
        var query = new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model");

        var fullText = await full.ObserveWithProvider(query, new ProfileStatusProvider("profile:build", "profile prompt"), context, CancellationToken.None);
        var runtimeText = await new StatusRegistry(runtime).ObserveWithProvider(query, null, context, CancellationToken.None);

        _ = await Assert.That(fullText).StartsWith("profile prompt\n\nGenerated at: ");
        _ = await Assert.That(fullText).Contains("\n\nRuntime:\n- agent: main (session)\n\nContext: 12% used");
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
    [Arguments("parent", "")]
    [Arguments("parent", "  ")]
    [Arguments("parent", "main-agent")]
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
        using var catalog = QueueCatalog("runtime-tree");
        using var root = catalog.Register(AgentIdentity.Main("root", "main", TestModels.PromptTemplates));
        using var child = catalog.Register(AgentIdentity.Child("child", "root", "main", "worker", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates));
        _ = root.Create("work", "queued work");
        _ = child.Create("results", string.Empty);
        IStatusProvider provider = new RuntimeTreeStatusProvider(
            catalog,
            new ProcessStatusSource(
                new ShellProcessStatusSnapshot("child", "fetch", "fetch", ActiveWorkState.Running),
                new ShellProcessStatusSnapshot("root", "build", "build", ActiveWorkState.Running)),
            new AgentStatusSource(new ActiveAgentSnapshot("child", "root", "worker")),
            TestModels.PromptTemplates);

        var observation = await provider.Observe(
            new StatusQuery("root", string.Empty, string.Empty, "build", "provider/model"),
            cancellationToken);

        _ = await Assert.That(observation.Text).IsEqualTo(
            """
            Runtime:
            - agent: main (root)
              - queue: work (0 items, description: "queued work")
              - process: root/build (shell, running, name: build)
              - agent: worker (child)
                - queue: results (0 items)
                - process: child/fetch (shell, running, name: fetch)
            """);
    }

    [Test]
    public async Task Runtime_tree_reports_only_the_root_agent_when_idle(CancellationToken cancellationToken)
    {
        using var catalog = QueueCatalog("runtime-empty");
        using var root = catalog.Register(AgentIdentity.Main("root", "main", TestModels.PromptTemplates));
        IStatusProvider provider = new RuntimeTreeStatusProvider(catalog, new ProcessStatusSource(), new AgentStatusSource(), TestModels.PromptTemplates);

        var observation = await provider.Observe(
            new StatusQuery("root", string.Empty, string.Empty, "build", "provider/model"),
            cancellationToken);

        _ = await Assert.That(observation.Text).IsEqualTo("Runtime:\n- agent: main (root)");
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

    private static AgentQueueCatalog QueueCatalog(string name)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "parrot-tests", name, Guid.NewGuid().ToString("n"))).FullName;
        var resources = new UserSessionResources(
            new StatePaths(root, root, root),
            UserSessionId.Parse(Guid.NewGuid().ToString("n")),
            ProjectWorkspace.FromLaunchDirectory(root));
        return new AgentQueueCatalog(resources);
    }

    private sealed class ProcessStatusSource(params ShellProcessStatusSnapshot[] snapshots) : IProcessStatusSource
    {
        public IReadOnlyList<ShellProcessStatusSnapshot> Snapshot() => snapshots;
    }

    private sealed class AgentStatusSource(params ActiveAgentSnapshot[] snapshots) : IAgentStatusSource
    {
        public IReadOnlyList<ActiveAgentSnapshot> ActiveSnapshot() => snapshots;
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
