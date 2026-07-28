namespace Parrot.Cli.Commands;

internal sealed class ApplicationExit(CancellationTokenSource source) : IApplicationExit
{
    public Task Exit(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return source.CancelAsync();
    }
}
