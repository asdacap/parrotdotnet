using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class RetryingProviderTests
{
    private static readonly LLMRequest Request = new() { Model = "m", Messages = [] };

    [Test]
    public async Task A_drop_before_any_output_reconnects(CancellationToken cancellationToken)
    {
        var scripted = new ReplayProvider(
            () => ThrowImmediately(new IOException("dropped")),
            () => Yield(LLMEvent.TextDelta("hi"), LLMEvent.Completed("stop", 1, 0, 1, "hi", [])));

        var events = await Drain(new RetryingProvider(scripted), cancellationToken);
        var text = string.Concat(events.Where(e => e.Kind == LLMEventKind.TextDelta).Select(e => e.Text));

        _ = await Assert.That(scripted.Calls).IsEqualTo(2);
        _ = await Assert.That(text).IsEqualTo("hi");
        _ = await Assert.That(events[^1].Kind).IsEqualTo(LLMEventKind.Completed);
    }

    [Test]
    public async Task A_drop_after_output_is_not_retried(CancellationToken cancellationToken)
    {
        var scripted = new ReplayProvider(
            () => YieldThenThrow(new IOException("mid-stream"), LLMEvent.TextDelta("partial")));

        ILLMProvider provider = new RetryingProvider(scripted);
        var events = new List<LLMEvent>();

        async Task Consume()
        {
            await foreach (var published in provider.Call(Request, cancellationToken))
            {
                events.Add(published);
            }
        }

        _ = await Assert.That(Consume).Throws<IOException>();
        _ = await Assert.That(scripted.Calls).IsEqualTo(1);
        _ = await Assert.That(events.Single().Text).IsEqualTo("partial");
    }

    [Test]
    [Arguments("usage_limit_reached")]
    [Arguments("invalid_request_error")]
    public async Task A_permanent_failure_is_not_retried(string errorType, CancellationToken cancellationToken)
    {
        var scripted = new ReplayProvider(
            () => ThrowImmediately(new ProviderHttpException(400, errorType, string.Empty, "no")));

        ILLMProvider provider = new RetryingProvider(scripted);

        _ = await Assert.That(async () => await Drain(provider, cancellationToken)).Throws<ProviderHttpException>();
        _ = await Assert.That(scripted.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("")]
    [Arguments("insufficient_quota")]
    public async Task Service_unavailable_uses_the_visible_overload_retry(
        string errorCode,
        CancellationToken cancellationToken)
    {
        var scripted = new ReplayProvider(
            () => ThrowImmediately(new ProviderHttpException(503, string.Empty, errorCode, "Service Unavailable")));
        ILLMProvider provider = new RetryingProvider(scripted);
        await using var enumerator = provider.Call(Request, cancellationToken).GetAsyncEnumerator(cancellationToken);

        var moved = await enumerator.MoveNextAsync();
        var retry = enumerator.Current;

        _ = await Assert.That(moved).IsTrue();
        _ = await Assert.That(scripted.Calls).IsEqualTo(1);
        _ = await Assert.That(retry.Kind).IsEqualTo(LLMEventKind.Retry);
        _ = await Assert.That(retry.Attempt).IsEqualTo(1);
        _ = await Assert.That(retry.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(2));
        _ = await Assert.That(retry.Text).IsEqualTo("Provider servers are overloaded. Retrying (attempt 1/5).");
    }

    [Test]
    public async Task Credential_availability_is_delegated_without_retry(CancellationToken cancellationToken)
    {
        var scripted = new ReplayProvider();
        ILLMProvider provider = new RetryingProvider(scripted);
        var available = await provider.HasCredential(cancellationToken);

        _ = await Assert.That(available).IsTrue();
        _ = await Assert.That(scripted.CredentialChecks).IsEqualTo(1);
    }

    // A structured error inside a 200 stream classifies exactly as an HTTP one.
    [Test]
    public async Task A_terminal_stream_error_surfaces(CancellationToken cancellationToken)
    {
        var scripted = new ReplayProvider(
            () => ThrowImmediately(new ProviderResponseException("insufficient_quota", string.Empty, "spent")));

        ILLMProvider provider = new RetryingProvider(scripted);

        _ = await Assert.That(async () => await Drain(provider, cancellationToken))
            .Throws<ProviderResponseException>();
        _ = await Assert.That(scripted.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task A_structured_stream_context_error_is_not_retried(CancellationToken cancellationToken)
    {
        var scripted = new ReplayProvider(
            () => ThrowImmediately(new ProviderResponseException(string.Empty, "context_window_exceeded", "too long")));

        ILLMProvider provider = new RetryingProvider(scripted);

        _ = await Assert.That(async () => await Drain(provider, cancellationToken))
            .Throws<ProviderResponseException>();
        _ = await Assert.That(scripted.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Seed_models_and_optional_usage_reporting_are_forwarded(
        bool reportsUsage,
        CancellationToken cancellationToken)
    {
        var reporter = new RecordingUsageReporter();
        IReadOnlyList<LLMModel> seed = [new LLMModel("seed", "scripted")];
        var scripted = new ReplayProvider
        {
            Models = seed,
            UsageReporter = reportsUsage ? reporter : null,
        };
        ILLMProvider provider = new RetryingProvider(scripted);

        _ = await Assert.That(provider.SeedModels().SequenceEqual(seed)).IsTrue();
        if (reportsUsage)
        {
            var forwarded = provider.UsageReporter
                ?? throw new InvalidOperationException("The usage reporter was not forwarded.");
            var usage = await forwarded.Usage(cancellationToken);
            _ = await Assert.That(usage.PlanType).IsEqualTo("subscription");
            _ = await Assert.That(reporter.Calls).IsEqualTo(1);
            _ = await Assert.That(reporter.CancellationToken).IsEqualTo(cancellationToken);
        }
        else
        {
            _ = await Assert.That(provider.UsageReporter).IsNull();
            _ = await Assert.That(reporter.Calls).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Stateless_sessions_report_unsupported_fallback_without_consuming_a_call(
        bool retry,
        CancellationToken cancellationToken)
    {
        var scripted = new ReplayProvider(() => Yield(LLMEvent.Completed("stop", 1, 0, 1, "reply", [])));
        ILLMProvider provider = retry ? new RetryingProvider(scripted) : scripted;
        await using var session = provider.OpenSession();

        _ = await Assert.That(await session.TryFallBackToHttp()).IsFalse();
        _ = await Assert.That(scripted.Calls).IsEqualTo(0);
        var events = new List<LLMEvent>();
        await foreach (var published in session.Call(Request, cancellationToken))
        {
            events.Add(published);
        }

        _ = await Assert.That(events.Single().Kind).IsEqualTo(LLMEventKind.Completed);
        _ = await Assert.That(scripted.Calls).IsEqualTo(1);
    }

    [Test]
    [Timeout(15_000)]
    public async Task Exhausted_websocket_retries_preserve_the_unsupported_session_fallback_error(
        CancellationToken cancellationToken)
    {
        var scripted = new ReplayProvider([.. Enumerable.Range(0, 6)
            .Select<int, Func<IAsyncEnumerable<LLMEvent>>>(_ =>
                () => ThrowImmediately(new ResponsesWebSocketTransportException("dropped")))]);
        ILLMProvider provider = new RetryingProvider(scripted);
        await using var session = provider.OpenSession();

        async Task Consume()
        {
            await foreach (var published in session.Call(Request, cancellationToken))
            {
                _ = published;
            }
        }

        _ = await Assert.That(Consume).ThrowsExactly<InvalidOperationException>()
            .WithMessage("A provider call requested HTTP fallback without supporting it.");
        _ = await Assert.That(scripted.Calls).IsEqualTo(6);
    }

    private static async Task<List<LLMEvent>> Drain(ILLMProvider provider, CancellationToken cancellationToken)
    {
        var events = new List<LLMEvent>();

        await foreach (var published in provider.Call(Request, cancellationToken))
        {
            events.Add(published);
        }

        return events;
    }

    private static async IAsyncEnumerable<LLMEvent> Yield(params LLMEvent[] events)
    {
        foreach (var published in events)
        {
            await Task.Yield();
            yield return published;
        }
    }

    private static async IAsyncEnumerable<LLMEvent> YieldThenThrow(Exception failure, params LLMEvent[] events)
    {
        foreach (var published in events)
        {
            await Task.Yield();
            yield return published;
        }

        throw failure;
    }

    private static async IAsyncEnumerable<LLMEvent> ThrowImmediately(Exception failure)
    {
        await Task.Yield();

        if (failure is not null)
        {
            throw failure;
        }

        yield break;
    }

    private sealed class RecordingUsageReporter : IUsageReporter
    {
        public int Calls { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<SubscriptionUsage> Usage(CancellationToken cancellationToken)
        {
            Calls++;
            CancellationToken = cancellationToken;
            return Task.FromResult(new SubscriptionUsage { PlanType = "subscription" });
        }
    }

    private sealed class ReplayProvider(params Func<IAsyncEnumerable<LLMEvent>>[] attempts) : ILLMProvider
    {
        private readonly Queue<Func<IAsyncEnumerable<LLMEvent>>> _attempts = new(attempts);

        public int Calls { get; private set; }

        public int CredentialChecks { get; private set; }

        public string Id => "scripted";

        public IReadOnlyList<LLMModel> Models { get; init; } = [];

        public IUsageReporter? UsageReporter { get; init; }

        public IReadOnlyList<LLMModel> SeedModels() => Models;

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken)
        {
            CredentialChecks++;
            return ValueTask.FromResult(true);
        }

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return _attempts.Dequeue()();
        }
    }
}
