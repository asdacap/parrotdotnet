using Parrot.Statuses;

namespace Parrot.Core.Tests;

internal sealed class StatusRegistryTests
{
    [Test]
    public async Task Observe_composes_available_status_by_key_and_resamples_providers()
    {
        var query = new StatusQuery("session", string.Empty, string.Empty, "plan", "openai", "gpt", "high");
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
            new StatusQuery("session", string.Empty, string.Empty, "build", "provider", "model", string.Empty),
            CancellationToken.None);

        _ = await Assert.That(observation.Available).IsEqualTo(available);
        _ = await Assert.That(observation.Text).IsEqualTo(expected);
    }

    [Test]
    [Arguments("", "")]
    [Arguments("parent", "")]
    [Arguments("parent", "  ")]
    [Arguments("parent", "main-agent")]
    public async Task Selection_reports_parent_context_when_present(string parentSessionId, string parentSessionName)
    {
        var observation = await new SelectionStatusProvider().Observe(
            new StatusQuery("child", parentSessionId, parentSessionName, "build", "provider", "model", string.Empty),
            CancellationToken.None);

        var parent = string.IsNullOrWhiteSpace(parentSessionId)
            ? string.Empty
            : string.IsNullOrWhiteSpace(parentSessionName)
                ? $"\nParent session: {parentSessionId}"
                : $"\nParent session: {parentSessionId} ({parentSessionName})";
        _ = await Assert.That(observation.Text).IsEqualTo($"Active profile: build\nModel: provider/model{parent}");
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
    public async Task Active_work_reports_processes_and_subagents_separately(CancellationToken cancellationToken)
    {
        var query = new StatusQuery("session", string.Empty, string.Empty, "build", "provider", "model", string.Empty);
        var registry = new StatusRegistry(
            new ActiveWorkStatusProvider(
                ActiveWorkKind.Agent,
                new ActiveWorkSource(
                    new ActiveWorkObservation("child-z", "zeta", ActiveWorkKind.Agent, ActiveWorkState.Running),
                    new ActiveWorkObservation("child-a", "alpha", ActiveWorkKind.Agent, ActiveWorkState.Running))),
            new ActiveWorkStatusProvider(
                ActiveWorkKind.Shell,
                new ActiveWorkSource(
                    new ActiveWorkObservation("session/z", "zeta", ActiveWorkKind.Shell, ActiveWorkState.Running),
                    new ActiveWorkObservation("session/a", "alpha", ActiveWorkKind.Shell, ActiveWorkState.Running))));

        var observed = await registry.Observe(query, null, cancellationToken);

        _ = await Assert.That(observed).IsEqualTo(
            """
            Active processes:
            - session/a (shell, running, name: alpha)
            - session/z (shell, running, name: zeta)

            Active subagents:
            - child-a (agent, running, name: alpha)
            - child-z (agent, running, name: zeta)
            """);
    }

    [Test]
    public async Task Active_work_reports_empty_process_and_subagent_sections(CancellationToken cancellationToken)
    {
        var query = new StatusQuery("session", string.Empty, string.Empty, "build", "provider", "model", string.Empty);
        var registry = new StatusRegistry(
            new ActiveWorkStatusProvider(ActiveWorkKind.Agent, new ActiveWorkSource()),
            new ActiveWorkStatusProvider(ActiveWorkKind.Shell, new ActiveWorkSource()));

        var observed = await registry.Observe(query, null, cancellationToken);

        _ = await Assert.That(observed).IsEqualTo("Active processes: none\n\nActive subagents: none");
    }

    [Test]
    public async Task Register_and_profile_reject_duplicate_keys()
    {
        var registry = new StatusRegistry(Provider("runtime:selection", "first"));

        _ = await Assert.That(() => registry.Register(Provider("runtime:selection", "second")))
            .Throws<StatusRegistryException>();
        _ = await Assert.That(async () => await registry.Observe(
                new StatusQuery("session", string.Empty, string.Empty, "build", "provider", "model", string.Empty),
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
                new StatusQuery("session", string.Empty, string.Empty, "build", "provider", "model", string.Empty),
                null,
                CancellationToken.None))
            .Throws<StatusRegistryException>();

        _ = await Assert.That(exception?.Message).Contains("runtime:failure");
        _ = await Assert.That(exception?.InnerException).IsTypeOf<InvalidOperationException>();
    }

    private static ScriptedStatusProvider Provider(string key, string text) =>
        new(key, (_, _) => ValueTask.FromResult(StatusObservation.AvailableText(text)));

    private sealed class ActiveWorkSource(params ActiveWorkObservation[] active) : IActiveWorkSource
    {
        public IReadOnlyList<ActiveWorkObservation> Active() => active;
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
