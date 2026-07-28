namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedLiveInputHost(
    ITerminal terminal,
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace) : ILiveInputHost
{
    private readonly byte[] _buffer = new byte[1024];
    private readonly TerminalKeyDecoder _decoder = new();
    private readonly Queue<TerminalKey> _keys = [];

    public async ValueTask<TerminalKey> ReadKey(CancellationToken cancellationToken)
    {
        TerminalKey key;
        while (!_keys.TryDequeue(out key))
        {
            var count = await terminal.Read(_buffer, cancellationToken).ConfigureAwait(false);
            var decoded = count == 0 ? _decoder.Flush() : _decoder.Feed(_buffer.AsSpan(0, count));
            foreach (var item in decoded)
            {
                _keys.Enqueue(item);
            }
        }

        return key;
    }

    public Task ReplaceInput(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken) =>
        replace(items, cancellationToken);
}
