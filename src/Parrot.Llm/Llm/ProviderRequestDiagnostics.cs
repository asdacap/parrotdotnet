using System.Diagnostics;
using System.Runtime.CompilerServices;
using Parrot.Diagnostics;

namespace Parrot.Llm;

internal sealed class ProviderRequestDiagnostics(IDiagnosticLog diagnostics, DiagnosticEvent call, LastRequestDumper? lastRequestDumper) : IProviderRequestDiagnostics
{
    public void DumpRequest(byte[] body) => lastRequestDumper?.Dump(body);

    public async IAsyncEnumerable<LLMEvent> Trace(
        Func<IProviderAttemptDiagnostics?, CancellationToken, IAsyncEnumerable<LLMEvent>> send,
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
        var recorded = false;
        IAsyncEnumerator<LLMEvent>? enumerator = null;

        // A cancellation can strand the enumerator below its finally: when the
        // consumer walks away, DisposeAsync may never run, so the terminal
        // record has to be written the moment cancellation fires.
        using var cancellationWatcher = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (cancellationWatcher is { } watcher)
        {
            _ = watcher.Token.Register(
                () => Record("cancelled", DiagnosticSeverity.Information, new OperationCanceledException("provider call cancelled while in flight")));
        }

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
                Record(
                    failure is OperationCanceledException ? "cancelled" : failure is null ? outcome : "failed",
                    failure is null or OperationCanceledException ? DiagnosticSeverity.Information : DiagnosticSeverity.Error,
                    failure);
            }
        }

        void Record(string terminalOutcome, DiagnosticSeverity severity, Exception? cause)
        {
            if (recorded)
            {
                return;
            }

            recorded = true;
            diagnostics.Write(entry with
            {
                Operation = "request_finished",
                RequestBytes = attempt.RequestBytes,
                ResponseBytes = attempt.ResponseBytes,
                Severity = severity,
                Outcome = terminalOutcome,
                ErrorCode = cause is null ? null : DiagnosticEvent.ClassifyFailure(cause),
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
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
