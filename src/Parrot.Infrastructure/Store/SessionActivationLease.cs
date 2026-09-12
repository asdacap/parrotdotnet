namespace Parrot.Store;

internal sealed class SessionActivationLease : IDisposable, IAsyncDisposable
{
    private readonly WorkingDirectoryClaim _claim;
    private int _released;

    internal SessionActivationLease(
        WorkingDirectoryClaim claim,
        OpenIntent intent,
        string leaseId,
        long generation,
        string runtimeInstanceId)
    {
        _claim = claim;
        Intent = intent;
        LeaseId = leaseId;
        Generation = generation;
        RuntimeInstanceId = runtimeInstanceId;
    }

    public OpenIntent Intent { get; }

    public string LeaseId { get; }

    public long Generation { get; }

    public string RuntimeInstanceId { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _claim.Release(this);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
