namespace Parrot.Core.Tests;

internal static class TestPolling
{
    public static async Task Until(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }
}
