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
        try
        {
            TerminalKey key;
            while (true)
            {
                ThrowIfCancellationRequested(cancellationToken);
                if (_keys.TryDequeue(out key))
                {
                    break;
                }

                var count = await terminal.Read(_buffer, cancellationToken).ConfigureAwait(false);
                ThrowIfCancellationRequested(cancellationToken);
                var decoded = count == 0 ? _decoder.Flush() : _decoder.Feed(_buffer.AsSpan(0, count));
                foreach (var item in decoded)
                {
                    _keys.Enqueue(item);
                }
            }

            ThrowIfCancellationRequested(cancellationToken);
            return key;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Reset();
            throw;
        }
    }

    public Task ReplaceInput(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken) =>
        replace(items, cancellationToken);

    public void ResetInput() => Reset();

    private void ThrowIfCancellationRequested(CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Reset();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Reset()
    {
        _keys.Clear();
        _decoder.Reset();
    }
}
