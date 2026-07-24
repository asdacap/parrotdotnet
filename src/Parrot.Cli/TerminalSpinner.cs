namespace Parrot.Cli;

internal sealed class TerminalSpinner(
    TerminalFrameRenderer renderer,
    Func<CancellationToken, Task> delay)
{
    private const int IntervalMilliseconds = 80;

    public TerminalSpinner(TerminalFrameRenderer renderer)
        : this(
            renderer,
            static cancellationToken => Task.Delay(IntervalMilliseconds, cancellationToken))
    {
    }

    public async Task Run(
        Func<int, TerminalFrame> frame,
        Func<Func<Task>, CancellationToken, Task> lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(lifetime);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var animation = Animate(frame, stopping.Token);

        async Task Stop()
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            await animation.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            await lifetime(Stop, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await Stop().ConfigureAwait(false);
        }
    }

    private async Task Animate(Func<int, TerminalFrame> frame, CancellationToken cancellationToken)
    {
        try
        {
            for (var index = 0; ; index++)
            {
                await renderer.Draw(frame(index), cancellationToken).ConfigureAwait(false);
                await delay(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await renderer.Clear(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
