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

        var provider = new RetryingProvider(scripted);
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

        var provider = new RetryingProvider(scripted);

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
        var provider = new RetryingProvider(scripted);
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
        var available = await new RetryingProvider(scripted).HasCredential(cancellationToken);

        _ = await Assert.That(available).IsTrue();
        _ = await Assert.That(scripted.CredentialChecks).IsEqualTo(1);
    }

    // A structured error inside a 200 stream classifies exactly as an HTTP one.
    [Test]
    public async Task A_terminal_stream_error_surfaces(CancellationToken cancellationToken)
    {
        var scripted = new ReplayProvider(
            () => ThrowImmediately(new ProviderResponseException("insufficient_quota", string.Empty, "spent")));

        var provider = new RetryingProvider(scripted);

        _ = await Assert.That(async () => await Drain(provider, cancellationToken))
            .Throws<ProviderResponseException>();
        _ = await Assert.That(scripted.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task A_structured_stream_context_error_is_not_retried(CancellationToken cancellationToken)
    {
        var scripted = new ReplayProvider(
            () => ThrowImmediately(new ProviderResponseException(string.Empty, "context_window_exceeded", "too long")));

        var provider = new RetryingProvider(scripted);

        _ = await Assert.That(async () => await Drain(provider, cancellationToken))
            .Throws<ProviderResponseException>();
        _ = await Assert.That(scripted.Calls).IsEqualTo(1);
    }

    private static async Task<List<LLMEvent>> Drain(RetryingProvider provider, CancellationToken cancellationToken)
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

    private sealed class ReplayProvider(params Func<IAsyncEnumerable<LLMEvent>>[] attempts) : ILLMProvider
    {
        private readonly Queue<Func<IAsyncEnumerable<LLMEvent>>> _attempts = new(attempts);

        public int Calls { get; private set; }

        public int CredentialChecks { get; private set; }

        public string Id => "scripted";

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
