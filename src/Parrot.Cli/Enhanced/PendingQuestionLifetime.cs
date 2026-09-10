using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Enhanced;

internal sealed class PendingQuestionLifetime(
    GeneratedParrot.ParrotClient client,
    string userSessionId,
    string requestId,
    TimeSpan reconciliationInterval,
    TimeProvider timeProvider,
    Func<long?, CancellationToken, Task> updateCountdown) : IDisposable
{
    private readonly CancellationTokenSource _closed = new();
    private readonly QuestionCountdown _countdown = new(timeProvider);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long? _displayedSeconds;

    public CancellationToken ClosedToken => _closed.Token;

    public bool IsClosed => _closed.IsCancellationRequested;

    public Task WaitUntilReady(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    public void Dispose() => _closed.Dispose();

    public async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !IsClosed)
            {
                try
                {
                    var listed = await client.ListPendingQuestionsAsync(
                        new ListPendingQuestionsRequest { UserSessionId = userSessionId },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    var pending = listed.Questions.FirstOrDefault(question =>
                        string.Equals(question.Id, requestId, StringComparison.Ordinal));
                    if (pending is null)
                    {
                        await _closed.CancelAsync().ConfigureAwait(false);
                        _ = _ready.TrySetResult();
                        return;
                    }

                    _countdown.Update(pending.HasRemainingTimeoutMs ? pending.RemainingTimeoutMs : null);
                    _ = _ready.TrySetResult();
                }
                catch (RpcException) when (!cancellationToken.IsCancellationRequested)
                {
                }
                catch (RpcException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var remainingSeconds = _countdown.GetRemainingSeconds();
                if (remainingSeconds != _displayedSeconds)
                {
                    await updateCountdown(remainingSeconds, cancellationToken).ConfigureAwait(false);
                    _displayedSeconds = remainingSeconds;
                }

                await Task.Delay(reconciliationInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _ = _ready.TrySetCanceled(cancellationToken);
        }
    }
}
