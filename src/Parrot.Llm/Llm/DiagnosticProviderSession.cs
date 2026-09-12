using System.Diagnostics;
using System.Runtime.CompilerServices;
using Parrot.Diagnostics;

namespace Parrot.Llm;

internal sealed class DiagnosticProviderSession(
    ILLMProviderSession providerSession,
    IDiagnosticLog diagnostics,
    string agentSessionId,
    string providerId,
    LastRequestDumper? lastRequestDumper) : ILLMProviderSession
{
    public void BeginTurn() => providerSession.BeginTurn();

    public ValueTask<bool> TryFallBackToHttp() => providerSession.TryFallBackToHttp();

    public ValueTask DisposeAsync() => providerSession.DisposeAsync();

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var entry = new DiagnosticEvent("provider", "call_started", DiagnosticSeverity.Information)
        {
            AgentSessionId = agentSessionId,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ProviderId = providerId,
            ModelId = request.Model,
        };
        diagnostics.Write(entry);
        var outcome = "disposed";
        Exception? failure = null;
        IAsyncEnumerator<LLMEvent>? enumerator = null;
        try
        {
            try
            {
                enumerator = providerSession.CallWithRetryObservation(
                        request with { Diagnostics = new ProviderRequestDiagnostics(diagnostics, entry, lastRequestDumper) },
                        ObserveRetry,
                        cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }

            while (true)
            {
                LLMEvent published;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        outcome = "completed";
                        break;
                    }

                    published = enumerator.Current;
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }

                if (published.Kind == LLMEventKind.Retry)
                {
                    ObserveRetry(published.Attempt, published.RetryAfter);
                }
                else if (published.Kind == LLMEventKind.Completed)
                {
                    diagnostics.Write(entry with { Operation = "input_tokens", Count = published.InputTokens });
                    diagnostics.Write(entry with { Operation = "cached_input_tokens", Count = published.CachedInputTokens });
                    diagnostics.Write(entry with { Operation = "output_tokens", Count = published.OutputTokens });
                }

                yield return published;
            }
        }
        finally
        {
            try
            {
                if (enumerator is not null)
                {
                    await DisposeStream(enumerator).ConfigureAwait(false);
                }
            }
            finally
            {
                diagnostics.Write(entry with
                {
                    Operation = "call_finished",
                    Severity = failure is null or OperationCanceledException
                        ? DiagnosticSeverity.Information
                        : DiagnosticSeverity.Error,
                    Outcome = failure is OperationCanceledException ? "cancelled" : failure is null ? outcome : "failed",
                    ErrorCode = failure is null ? null : DiagnosticEvent.ClassifyFailure(failure),
                    DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                });
            }
        }

        void ObserveRetry(int attempt, TimeSpan delay) => diagnostics.Write(entry with
        {
            Operation = "call_retry",
            Severity = DiagnosticSeverity.Warning,
            Count = attempt,
            DurationMilliseconds = (long)delay.TotalMilliseconds,
        });

        async ValueTask DisposeStream(IAsyncEnumerator<LLMEvent> stream)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
        }
    }
}
