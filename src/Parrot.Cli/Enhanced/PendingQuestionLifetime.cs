using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Enhanced;

internal sealed class PendingQuestionLifetime(
    GeneratedParrot.ParrotClient client,
    string userSessionId,
    string requestId,
    TimeSpan reconciliationInterval) : IDisposable
{
    private readonly CancellationTokenSource _closed = new();

    public CancellationToken ClosedToken => _closed.Token;

    public bool IsClosed => _closed.IsCancellationRequested;

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
                    if (!listed.Questions.Any(question =>
                            string.Equals(question.Id, requestId, StringComparison.Ordinal)))
                    {
                        await _closed.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
                catch (RpcException) when (!cancellationToken.IsCancellationRequested)
                {
                }
                catch (RpcException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await Task.Delay(reconciliationInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
