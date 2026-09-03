using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class TestSlashActivity : ISlashActivity
{
    public int Waits { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public Task WaitUntilIdle(CancellationToken cancellationToken)
    {
        Waits++;
        CancellationToken = cancellationToken;
        return Task.CompletedTask;
    }
}
