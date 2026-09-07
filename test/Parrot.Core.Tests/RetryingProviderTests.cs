using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class RetryingProviderTests
{
    private static readonly LLMRequest Request = new() { Model = "m", Messages = [] };

    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(true, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, true, true)]
    public async Task Context_budget_recovery_is_bounded_and_preserves_the_request(
        bool useSession, bool failsAgain, bool streamError, CancellationToken cancellationToken)
    {
        const string detail = "litellm.BadRequestError: OpenAIException - Requested token count exceeds the model's maximum context length of 180000 tokens. You requested a total of 180514 tokens: 147746 tokens from the input messages and 32768 tokens for the completion. Please reduce your prompt.";
        Exception failure = streamError
            ? new ProviderResponseException("null", "400", detail)
            : new ProviderHttpException(400, "null", "400", detail + " Fallback failed: " + detail);
        const string secondDetail = "Requested token count exceeds the model's maximum context length of 180000 tokens. You requested a total of 180001 tokens: 155135 tokens from the input messages and 24866 tokens for the completion.";
        Exception secondFailure = streamError
            ? new ProviderResponseException("null", "400", secondDetail)
            : new ProviderHttpException(400, "null", "400", secondDetail);
        var scripted = new ReplayProvider(
            () => ThrowImmediately(failure),
            () => failsAgain
                ? ThrowImmediately(secondFailure)
                : Yield(LLMEvent.Completed("stop", 147746, 0, 1, "reply", [])));
        ILLMProvider provider = new RetryingProvider(scripted);
        await using var session = provider.OpenSession();
        var request = Request with { MaxTokens = 32768, Instructions = "preserve", IncludeRouterMetadata = true };
        var events = new List<LLMEvent>();

        async Task Consume()
        {
            var stream = useSession ? session.Call(request, cancellationToken) : provider.Call(request, cancellationToken);
            await foreach (var published in stream)
            {
                events.Add(published);
            }
        }

        if (failsAgain)
        {
            if (streamError)
            {
                _ = await Assert.That(Consume).Throws<ProviderResponseException>();
            }
            else
            {
                _ = await Assert.That(Consume).Throws<ProviderHttpException>();
            }

            _ = await Assert.That(events).IsEmpty();
        }
        else
        {
            await Consume();
            _ = await Assert.That(events.Single().Kind).IsEqualTo(LLMEventKind.Completed);
        }

        _ = await Assert.That(scripted.Calls).IsEqualTo(2);
        _ = await Assert.That(scripted.Requests[0]).IsEqualTo(request);
        _ = await Assert.That(scripted.Requests[1]).IsEqualTo(request with { MaxTokens = 24866 });
        _ = await Assert.That(request.MaxTokens).IsEqualTo(32768);
    }

    [Test]
    [Arguments(false, "180001", "212769")]
    [Arguments(true, "180001", "212769")]
    [Arguments(false, "171429", "204197")]
    [Arguments(true, "171429", "204197")]
    public async Task Context_errors_without_output_capacity_are_not_retried(
        bool useSession, string input, string total, CancellationToken cancellationToken)
    {
        var failure = new ProviderHttpException(
            400,
            "null",
            "400",
            $"Requested token count exceeds the model's maximum context length of 180000 tokens. You requested a total of {total} tokens: {input} tokens from the input messages and 32768 tokens for the completion.");
        var scripted = new ReplayProvider(() => ThrowImmediately(failure));
        ILLMProvider provider = new RetryingProvider(scripted);
        await using var session = provider.OpenSession();
        var request = Request with { MaxTokens = 32768 };

        async Task Consume()
        {
            var stream = useSession ? session.Call(request, cancellationToken) : provider.Call(request, cancellationToken);
            await foreach (var published in stream)
            {
                _ = published;
            }
        }

        _ = await Assert.That(Consume).Throws<ProviderHttpException>();
        _ = await Assert.That(scripted.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, LLMEventKind.TextDelta)]
    [Arguments(true, LLMEventKind.TextDelta)]
    [Arguments(false, LLMEventKind.ReasoningDelta)]
    [Arguments(true, LLMEventKind.ReasoningDelta)]
    [Arguments(false, LLMEventKind.ToolCallDelta)]
    [Arguments(true, LLMEventKind.ToolCallDelta)]
    [Arguments(false, LLMEventKind.Completed)]
    [Arguments(true, LLMEventKind.Completed)]
    public async Task Context_errors_after_visible_output_are_not_retried(
        bool useSession, LLMEventKind kind, CancellationToken cancellationToken)
    {
        var failure = new ProviderHttpException(
            400,
            "null",
            "400",
            "Requested token count exceeds the model's maximum context length of 180000 tokens. You requested a total of 180514 tokens: 147746 tokens from the input messages and 32768 tokens for the completion.");
        var scripted = new ReplayProvider(() => YieldThenThrow(failure, new LLMEvent { Kind = kind }));
        ILLMProvider provider = new RetryingProvider(scripted);
        await using var session = provider.OpenSession();
        var request = Request with { MaxTokens = 32768 };

        async Task Consume()
        {
            var stream = useSession ? session.Call(request, cancellationToken) : provider.Call(request, cancellationToken);
            await foreach (var published in stream)
            {
                _ = published;
            }
        }

        _ = await Assert.That(Consume).Throws<ProviderHttpException>();
        _ = await Assert.That(scripted.Calls).IsEqualTo(1);
    }

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

        public List<LLMRequest> Requests { get; } = [];

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
            Requests.Add(request);
            return _attempts.Dequeue()();
        }
    }
}
