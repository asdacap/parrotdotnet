using System.Threading.Channels;

namespace Parrot.Cli.Enhanced;

internal sealed class LiveUpdateScheduler
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1d / 30d);

    private readonly Func<CancellationToken, Task> _draw;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Channel<bool> _invalidations = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public LiveUpdateScheduler(Func<CancellationToken, Task> draw)
        : this(draw, static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal LiveUpdateScheduler(
        Func<CancellationToken, Task> draw,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _draw = draw;
        _delay = delay;
    }

    public bool Invalidate() => _invalidations.Writer.TryWrite(true);

    public async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            while (await _invalidations.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = _invalidations.Reader.TryRead(out _);
                await _delay(Interval, cancellationToken).ConfigureAwait(false);
                while (_invalidations.Reader.TryRead(out _))
                {
                }

                await _draw(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
