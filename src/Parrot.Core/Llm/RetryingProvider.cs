using System.Runtime.CompilerServices;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

// Wraps a provider so a call that fails before delivering client-visible output
// is retried, without ever duplicating output that already reached the client.
// It folds Go's two retry layers into one: header-timeout retries (unbounded,
// 2s..30s), transient engine-overload retries (<=5, shared budget), and stream
// reconnects on a dropped connection (<=5). Usage and router metadata are
// bookkeeping and do not count as visible output.
internal sealed class RetryingProvider(ILLMProvider inner) : ILLMProvider
{
    private const int StreamMaxRetries = 5;
    private const int OverloadMaxRetries = 5;
    private static readonly TimeSpan StreamRetryBaseDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan HeaderRetryInitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeaderRetryMaximumDelay = TimeSpan.FromSeconds(30);

    public string Id => inner.Id;

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
        inner.HasCredential(cancellationToken);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        inner.ListModels(cancellationToken);

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = new RetryState();

        while (true)
        {
            Advance retry;
            var enumerator = inner.Call(request, cancellationToken).GetAsyncEnumerator(cancellationToken);

            try
            {
                while (true)
                {
                    var step = await Step(enumerator, state, cancellationToken).ConfigureAwait(false);

                    if (step.Retry)
                    {
                        retry = step;
                        break;
                    }

                    if (step.End)
                    {
                        yield break;
                    }

                    foreach (var published in step.Emit ?? [])
                    {
                        if (IsVisible(published))
                        {
                            state.OutputEmitted = true;
                        }

                        yield return published;
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            // A retry notice is surfaced only for overload retries, matching
            // upstream; header-timeout and reconnect retries wait silently.
            if (retry.Reason.Length > 0)
            {
                yield return LLMEvent.Retry(retry.Attempt, retry.Delay, retry.Reason);
            }

            await Task.Delay(retry.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Advance> Step(
        IAsyncEnumerator<LLMEvent> enumerator, RetryState state, CancellationToken cancellationToken)
    {
        bool moved;

        try
        {
            moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            if (state.OutputEmitted || cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return Classify(failure, state);
        }

        if (!moved)
        {
            return Advance.Ended;
        }

        return Advance.Emitting([enumerator.Current]);
    }

    private static Advance Classify(Exception failure, RetryState state)
    {
        switch (failure)
        {
            case HeaderTimeoutException:
                var headerDelay = HeaderDelay(state.TimeoutAttempt);
                state.TimeoutAttempt++;

                // No reason: a header retry is silent, per upstream.
                return Advance.Retrying(headerDelay, state.TimeoutAttempt, string.Empty);

            case ProviderHttpException or ProviderResponseException when ProviderErrors.IsUsageLimit(failure):
                throw failure;

            case ProviderHttpException or ProviderResponseException when ProviderErrors.IsEngineOverloaded(failure):
                if (state.TakeOverload(out var overloadAttempt))
                {
                    return Advance.Retrying(OverloadDelay(overloadAttempt), overloadAttempt, OverloadReason(overloadAttempt));
                }

                throw failure;

            case ProviderHttpException http:
                if (RetryableStatus(http.StatusCode) && state.TakeStream(out var httpStreamAttempt))
                {
                    return Advance.Retrying(StreamDelay(httpStreamAttempt), httpStreamAttempt, string.Empty);
                }

                throw failure;

            // A missing key or bad configuration is not transient; retrying it
            // only delays the inevitable.
            case LLMProviderException:
                throw failure;

            default:
                // Network and protocol errors are retryable by default.
                if (state.TakeStream(out var streamAttempt))
                {
                    return Advance.Retrying(StreamDelay(streamAttempt), streamAttempt, string.Empty);
                }

                throw failure;
        }
    }

    private static bool IsVisible(LLMEvent published) =>
        published.Kind is LLMEventKind.TextDelta or LLMEventKind.ReasoningDelta
            or LLMEventKind.ToolCallDelta or LLMEventKind.Completed;

    private static bool RetryableStatus(int status) => status is 429 or >= 500;

    private static string OverloadReason(int attempt) =>
        $"Provider servers are overloaded. Retrying (attempt {attempt}/{OverloadMaxRetries}).";

    private static TimeSpan HeaderDelay(int timeoutAttempt) =>
        Capped(HeaderRetryInitialDelay, Math.Min(timeoutAttempt, 4), HeaderRetryMaximumDelay);

    private static TimeSpan OverloadDelay(int attempt) =>
        Capped(HeaderRetryInitialDelay, Math.Min(attempt - 1, 4), HeaderRetryMaximumDelay);

    private static TimeSpan StreamDelay(int attempt) =>
        TimeSpan.FromTicks(StreamRetryBaseDelay.Ticks << Math.Min(attempt - 1, 7));

    private static TimeSpan Capped(TimeSpan initial, int shift, TimeSpan maximum)
    {
        var delay = TimeSpan.FromTicks(initial.Ticks << shift);
        return delay > maximum ? maximum : delay;
    }

    private sealed class RetryState
    {
        private int _overloadAttempts;
        private int _streamRemaining = StreamMaxRetries;
        private int _streamAttempt;

        public bool OutputEmitted { get; set; }

        public int TimeoutAttempt { get; set; }

        public bool TakeOverload(out int attempt)
        {
            if (_overloadAttempts >= OverloadMaxRetries)
            {
                attempt = _overloadAttempts;
                return false;
            }

            _overloadAttempts++;
            attempt = _overloadAttempts;
            return true;
        }

        public bool TakeStream(out int attempt)
        {
            if (_streamRemaining == 0)
            {
                attempt = 0;
                return false;
            }

            _streamRemaining--;
            _streamAttempt++;
            attempt = _streamAttempt;
            return true;
        }
    }

    private sealed record Advance
    {
        public static Advance Ended { get; } = new() { End = true };

        public IReadOnlyList<LLMEvent>? Emit { get; init; }

        public bool End { get; init; }

        public bool Retry { get; init; }

        public TimeSpan Delay { get; init; }

        public int Attempt { get; init; }

        public string Reason { get; init; } = string.Empty;

        public static Advance Emitting(IReadOnlyList<LLMEvent> emit) => new() { Emit = emit };

        public static Advance Retrying(TimeSpan delay, int attempt, string reason) =>
            new() { Retry = true, Delay = delay, Attempt = attempt, Reason = reason };
    }
}
