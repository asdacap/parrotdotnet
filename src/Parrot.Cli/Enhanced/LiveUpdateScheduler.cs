namespace Parrot.Cli.Enhanced;

internal sealed class LiveUpdateScheduler : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1d / 30d);

    private readonly Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> _draw;
    private readonly Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> _commit;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _publishing = new(1, 1);
    private readonly SemaphoreSlim _state = new(1, 1);

    private CancellationTokenSource? _timerCancellation;
    private Task? _timer;
    private MarkdownLiveUpdate? _pending;
    private bool _disposed;

    public LiveUpdateScheduler(
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit)
        : this(draw, commit, static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal LiveUpdateScheduler(
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _draw = draw;
        _commit = commit;
        _delay = delay;
    }

    public Task Publish(MarkdownLiveUpdate update, CancellationToken cancellationToken) =>
        update.Scrollback is { } scrollback
            ? Commit(scrollback, update.Preview, cancellationToken)
            : Queue(update, cancellationToken);

    public async Task Flush(CancellationToken cancellationToken)
    {
        Task? timer;
        CancellationTokenSource? timerCancellation;
        MarkdownLiveUpdate? pending;
        await _state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            timer = _timer;
            timerCancellation = _timerCancellation;
            pending = _pending;
            _timer = null;
            _timerCancellation = null;
            _pending = null;
        }
        finally
        {
            _ = _state.Release();
        }

        if (timerCancellation is not null)
        {
            await timerCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (timer is not null)
        {
            await timer.ConfigureAwait(false);
        }

        timerCancellation?.Dispose();
        if (pending is { } update)
        {
            await Draw(update.Preview, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Flush(CancellationToken.None).ConfigureAwait(false);
        await _publishing.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _ = _publishing.Release();
        _publishing.Dispose();
        _state.Dispose();
    }

    private static List<ILiveBufferItem> Items(IReadOnlyList<string> preview) =>
        [.. preview.Select(value => (ILiveBufferItem)new LiveTextValue(value))];

    private async Task Queue(MarkdownLiveUpdate update, CancellationToken cancellationToken)
    {
        await _state.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending = update;
            if (_timer is null)
            {
                StartTimer();
            }
        }
        finally
        {
            _ = _state.Release();
        }
    }

    private async Task Commit(
        IScrollbackItem scrollback,
        IReadOnlyList<string> preview,
        CancellationToken cancellationToken)
    {
        await Flush(cancellationToken).ConfigureAwait(false);
        await _publishing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _commit(scrollback, Items(preview), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _publishing.Release();
        }
    }

    private void StartTimer()
    {
        var cancellation = new CancellationTokenSource();
        _timerCancellation = cancellation;
        _timer = PublishAfterDelay(cancellation);
    }

    private async Task PublishAfterDelay(CancellationTokenSource cancellation)
    {
        try
        {
            while (true)
            {
                await _delay(Interval, cancellation.Token).ConfigureAwait(false);
                MarkdownLiveUpdate? update;
                await _state.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(_timerCancellation, cancellation))
                    {
                        return;
                    }

                    update = _pending;
                    _pending = null;
                }
                finally
                {
                    _ = _state.Release();
                }

                if (update is not null)
                {
                    await Draw(update.Value.Preview, CancellationToken.None).ConfigureAwait(false);
                }

                await _state.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(_timerCancellation, cancellation))
                    {
                        return;
                    }

                    if (_pending is null)
                    {
                        _timer = null;
                        _timerCancellation = null;
                        return;
                    }
                }
                finally
                {
                    _ = _state.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task Draw(IReadOnlyList<string> preview, CancellationToken cancellationToken)
    {
        await _publishing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _draw(Items(preview), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _publishing.Release();
        }
    }
}
