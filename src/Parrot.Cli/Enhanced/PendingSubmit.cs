namespace Parrot.Cli.Enhanced;

internal sealed class PendingSubmit
{
    private readonly CancellationTokenSource _cancellation;

    public PendingSubmit(
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task = Run(delay, interval, _cancellation.Token);
    }

    public Task Task { get; }

    public Task Complete() => Finish(false);

    public Task Cancel() => Finish(true);

    private static async Task Run(
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan interval,
        CancellationToken cancellationToken) =>
        await delay(interval, cancellationToken).ConfigureAwait(false);

    private async Task Finish(bool cancel)
    {
        if (cancel)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }
}
