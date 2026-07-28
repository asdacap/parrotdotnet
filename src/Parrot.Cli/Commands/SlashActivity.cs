namespace Parrot.Cli.Commands;

internal sealed class SlashActivity(Func<bool> isBusy) : ISlashActivity
{
    private const int PollIntervalMilliseconds = 20;

    public async Task WaitUntilIdle(CancellationToken cancellationToken)
    {
        while (isBusy())
        {
            await Task.Delay(PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }
}
