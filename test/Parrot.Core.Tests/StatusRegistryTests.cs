using Parrot.Agent;
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
        var profile = new ProfileStatusProvider("profile:plan", "profile prompt");

        var first = await registry.Observe(query, profile, CancellationToken.None);
        var second = await registry.Observe(query, profile, CancellationToken.None);

        _ = await Assert.That(first).IsEqualTo("profile prompt\n\nselection 1");
        _ = await Assert.That(second).IsEqualTo("profile prompt\n\nselection 2");
        _ = await Assert.That(observedQuery).IsEqualTo(query);
    }

    [Test]
    [Arguments("profile prompt", true, "profile prompt")]
    [Arguments("  ", false, "")]
    public async Task Profile_reports_only_a_nonblank_prompt(string prompt, bool available, string expected)
    {
        var observation = await new ProfileStatusProvider("profile:build", prompt).Observe(
            new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model"),
            CancellationToken.None);

        _ = await Assert.That(observation.Available).IsEqualTo(available);
        _ = await Assert.That(observation.Text).IsEqualTo(expected);
    }

    [Test]
    public async Task Selection_reports_a_complete_canonical_requested_selector()
    {
        var observation = await new SelectionStatusProvider(TestModels.PromptTemplates).Observe(
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
        var observation = await new SelectionStatusProvider(TestModels.PromptTemplates).Observe(
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
        var provider = Provider(key, "status");

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
        var provider = new RuntimeTreeStatusProvider(
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
        var provider = new RuntimeTreeStatusProvider(catalog, new ProcessStatusSource(), new AgentStatusSource(), TestModels.PromptTemplates);

        var observation = await provider.Observe(
            new StatusQuery("root", string.Empty, string.Empty, "build", "provider/model"),
            cancellationToken);

        _ = await Assert.That(observation.Text).IsEqualTo("Runtime:\n- agent: main (root)");
    }

    [Test]
    public async Task Register_and_profile_reject_duplicate_keys()
    {
        var registry = new StatusRegistry(Provider("runtime:selection", "first"));

        _ = await Assert.That(() => registry.Register(Provider("runtime:selection", "second")))
            .Throws<StatusRegistryException>();
        _ = await Assert.That(async () => await registry.Observe(
                new StatusQuery("session", string.Empty, string.Empty, "build", "provider/model"),
                Provider("runtime:selection", "profile"),
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

    private static ScriptedStatusProvider Provider(string key, string text) =>
        new(key, (_, _) => ValueTask.FromResult(StatusObservation.AvailableText(text)));

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
