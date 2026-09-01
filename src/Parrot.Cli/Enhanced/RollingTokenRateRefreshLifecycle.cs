namespace Parrot.Cli.Enhanced;

internal sealed class RollingTokenRateRefreshLifecycle(
    RollingTokenRateWindow window,
    Func<CancellationToken, Task> invalidate,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    private readonly RollingTokenRateWindow _window = window ?? throw new ArgumentNullException(nameof(window));
    private readonly Func<CancellationToken, Task> _invalidate = invalidate ?? throw new ArgumentNullException(nameof(invalidate));
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    private readonly object _lifecycle = new();
    private CancellationTokenSource? _cancellation;
    private Task _refreshing = Task.CompletedTask;

    public void EnsureRefreshing(CancellationToken cancellationToken)
    {
        if (!_window.HasRetainedSamples)
        {
            return;
        }

        lock (_lifecycle)
        {
            if (_cancellation is not null)
            {
                return;
            }

            var refreshingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellation = refreshingCancellation;
            _refreshing = Refresh(refreshingCancellation);
        }
    }

    public Task ShutdownAsync() => CancelAsync();

    public Task ResetAsync()
    {
        var cancelled = CancelAsync();
        _window.Reset();
        return cancelled;
    }

    private async Task CancelAsync()
    {
        Task refreshing;
        CancellationTokenSource? cancellation;
        lock (_lifecycle)
        {
            cancellation = _cancellation;
            refreshing = _refreshing;
            _cancellation = null;
            _refreshing = Task.CompletedTask;
        }

        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await refreshing.ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    private async Task Refresh(CancellationTokenSource refreshingCancellation)
    {
        var cancellationToken = refreshingCancellation.Token;
        try
        {
            while (true)
            {
                while (_window.HasRetainedSamples)
                {
                    await _delay(_window.GetExpiryDelay(), cancellationToken).ConfigureAwait(false);
                    _window.Advance();
                    await _invalidate(cancellationToken).ConfigureAwait(false);
                }

                lock (_lifecycle)
                {
                    if (!ReferenceEquals(_cancellation, refreshingCancellation))
                    {
                        return;
                    }

                    if (_window.HasRetainedSamples)
                    {
                        continue;
                    }

                    _cancellation = null;
                    _refreshing = Task.CompletedTask;
                    refreshingCancellation.Dispose();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_lifecycle)
            {
                if (ReferenceEquals(_cancellation, refreshingCancellation))
                {
                    _cancellation = null;
                    _refreshing = Task.CompletedTask;
                    refreshingCancellation.Dispose();
                }
            }
        }
    }
}
