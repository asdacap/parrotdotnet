using System.Diagnostics;
using System.Runtime.CompilerServices;
using Parrot.Diagnostics;

namespace Parrot.Llm;

internal sealed class ProviderRequestDiagnostics(IDiagnosticLog diagnostics, DiagnosticEvent call, LastRequestDumper? lastRequestDumper)
{
    public void DumpRequest(byte[] body) => lastRequestDumper?.Dump(body);

    public async IAsyncEnumerable<LLMEvent> Trace(
        Func<ProviderAttemptDiagnostics?, CancellationToken, IAsyncEnumerable<LLMEvent>> send,
        string transport,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var entry = call with
        {
            Operation = "request_started",
            RequestId = Guid.NewGuid().ToString("N"),
            Transport = transport,
        };
        var attempt = new ProviderAttemptDiagnostics(diagnostics, entry);
        diagnostics.Write(entry);
        var outcome = "disposed";
        Exception? failure = null;
        IAsyncEnumerator<LLMEvent>? enumerator = null;
        try
        {
            try
            {
                enumerator = send(attempt, cancellationToken).GetAsyncEnumerator(cancellationToken);
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
                    Operation = "request_finished",
                    RequestBytes = attempt.RequestBytes,
                    ResponseBytes = attempt.ResponseBytes,
                    Severity = failure is null or OperationCanceledException
                        ? DiagnosticSeverity.Information
                        : DiagnosticSeverity.Error,
                    Outcome = failure is OperationCanceledException ? "cancelled" : failure is null ? outcome : "failed",
                    ErrorCode = failure is null ? null : DiagnosticEvent.ClassifyFailure(failure),
                    DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                });
            }
        }

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
