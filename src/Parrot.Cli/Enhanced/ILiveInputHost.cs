namespace Parrot.Cli.Enhanced;

internal interface ILiveInputHost
{
    ValueTask<TerminalKey> ReadKey(CancellationToken cancellationToken);

    Task ReplaceInput(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken);

    void ResetInput();
}
