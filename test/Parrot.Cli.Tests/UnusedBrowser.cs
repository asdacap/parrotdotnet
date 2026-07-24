using Parrot.Auth;

namespace Parrot.Cli.Tests;

internal sealed class UnusedBrowser : IBrowserOpener
{
    public Task Open(string url, CancellationToken cancellationToken) =>
        throw new NotSupportedException("driving the loop opens no browser");
}
