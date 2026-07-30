namespace Parrot.Cli.Enhanced;

internal sealed class TerminalSpinner(
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
    Func<CancellationToken, Task> delay)
{
    private const int IntervalMilliseconds = 80;

    public TerminalSpinner(Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw)
        : this(
            draw,
            static cancellationToken => Task.Delay(IntervalMilliseconds, cancellationToken))
    {
    }

    public async Task Run(
        Func<int, ILiveBufferItem> frame,
        Func<Func<Task>, CancellationToken, Task> lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(lifetime);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var animation = Animate(frame, stopping.Token);
        var preserveFrame = false;

        async Task StopAnimation()
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            await animation.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        async Task PreserveFrame()
        {
            preserveFrame = true;
            await StopAnimation().ConfigureAwait(false);
        }

        try
        {
            await lifetime(PreserveFrame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await StopAnimation().ConfigureAwait(false);
            if (!preserveFrame)
            {
                await draw([], CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task Animate(Func<int, ILiveBufferItem> frame, CancellationToken cancellationToken)
    {
        try
        {
            for (var index = 0; ; index++)
            {
                await draw([frame(index)], cancellationToken).ConfigureAwait(false);
                await delay(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
