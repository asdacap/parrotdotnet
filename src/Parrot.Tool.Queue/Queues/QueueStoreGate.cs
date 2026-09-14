namespace Parrot.Queues;

internal sealed class QueueStoreGate : IDisposable
{
    private readonly Lock _lifecycle = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposeRequested;
    private int _users;

    public void Retain() => BeginWait();

    public void ReleaseRetention() => EndWait();

    public void Wait()
    {
        BeginWait();
        WaitCore();
    }

    public void WaitAfterDispose()
    {
        BeginWaitAfterDispose();
        WaitCore();
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        BeginWait();
        try
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            EndWait();
            throw;
        }
    }

    public bool TryWait()
    {
        BeginWait();
        if (_semaphore.Wait(0))
        {
            return true;
        }

        EndWait();
        return false;
    }

    public void Release()
    {
        _ = _semaphore.Release();
        EndWait();
    }

    public void Dispose()
    {
        bool dispose;
        lock (_lifecycle)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            dispose = _users == 0;
        }

        if (dispose)
        {
            _semaphore.Dispose();
        }
    }

    private void BeginWait()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _users++;
        }
    }

    private void BeginWaitAfterDispose()
    {
        lock (_lifecycle)
        {
            _users++;
        }
    }

    private void WaitCore()
    {
        try
        {
            _semaphore.Wait();
        }
        catch
        {
            EndWait();
            throw;
        }
    }

    private void EndWait()
    {
        bool dispose;
        lock (_lifecycle)
        {
            _users--;
            dispose = _disposeRequested && _users == 0;
        }

        if (dispose)
        {
            _semaphore.Dispose();
        }
    }
}
