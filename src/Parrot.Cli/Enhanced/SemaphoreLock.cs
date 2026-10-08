namespace Parrot.Cli.Enhanced;

internal static class SemaphoreLock
{
    public static async Task<SemaphoreRelease> Lock(this SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SemaphoreRelease(semaphore);
    }

    internal readonly record struct SemaphoreRelease(SemaphoreSlim Semaphore) : IDisposable
    {
        public void Dispose() => _ = Semaphore.Release();
    }
}
