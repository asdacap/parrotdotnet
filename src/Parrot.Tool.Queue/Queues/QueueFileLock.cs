namespace Parrot.Queues;

internal sealed class QueueFileLock(string path) : IDisposable
{
    public void Dispose()
    {
        if (path.Length == 0)
        {
            return;
        }

        try
        {
            Directory.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
