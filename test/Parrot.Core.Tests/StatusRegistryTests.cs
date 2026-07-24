using Parrot.Statuses;

namespace Parrot.Core.Tests;

internal sealed class StatusRegistryTests
{
    [Test]
    public async Task Observe_composes_available_status_by_key_and_resamples_providers()
    {
        var query = new StatusQuery("session", "plan", "openai", "gpt", "high");
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
        var profile = new ScriptedStatusProvider(
            "profile:plan",
            static (_, _) => ValueTask.FromResult(StatusObservation.AvailableText("profile")));

        var first = await registry.Observe(query, profile, CancellationToken.None);
        var second = await registry.Observe(query, profile, CancellationToken.None);

        _ = await Assert.That(first).IsEqualTo("profile\n\nselection 1");
        _ = await Assert.That(second).IsEqualTo("profile\n\nselection 2");
        _ = await Assert.That(observedQuery).IsEqualTo(query);
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
    public async Task Register_and_profile_reject_duplicate_keys()
    {
        var registry = new StatusRegistry(Provider("runtime:selection", "first"));

        _ = await Assert.That(() => registry.Register(Provider("runtime:selection", "second")))
            .Throws<StatusRegistryException>();
        _ = await Assert.That(async () => await registry.Observe(
                new StatusQuery("session", "build", "provider", "model", string.Empty),
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
                new StatusQuery("session", "build", "provider", "model", string.Empty),
                null,
                CancellationToken.None))
            .Throws<StatusRegistryException>();

        _ = await Assert.That(exception?.Message).Contains("runtime:failure");
        _ = await Assert.That(exception?.InnerException).IsTypeOf<InvalidOperationException>();
    }

    private static ScriptedStatusProvider Provider(string key, string text) =>
        new(key, (_, _) => ValueTask.FromResult(StatusObservation.AvailableText(text)));

    private sealed class ScriptedStatusProvider(
        string key,
        Func<StatusQuery, CancellationToken, ValueTask<StatusObservation>> observe) : IStatusProvider
    {
        public string Key { get; } = key;

        public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken) =>
            observe(query, cancellationToken);
    }
}
