using System.Text;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class ScriptedLiveInputHost(params string[] input) : ILiveInputHost
{
    private readonly TerminalKeyDecoder _decoder = new();
    private readonly Queue<byte[]> _input = new(input.Select(Encoding.UTF8.GetBytes));
    private readonly Queue<TerminalKey> _keys = [];

    public List<IReadOnlyList<ILiveBufferItem>> Frames { get; } = [];

    public ValueTask<TerminalKey> ReadKey(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TerminalKey key;
        while (!_keys.TryDequeue(out key))
        {
            if (!_input.TryDequeue(out var next))
            {
                foreach (var flushed in _decoder.Flush())
                {
                    _keys.Enqueue(flushed);
                }

                continue;
            }

            foreach (var decoded in _decoder.Feed(next))
            {
                _keys.Enqueue(decoded);
            }
        }

        return ValueTask.FromResult(key);
    }

    public Task ReplaceInput(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Frames.Add([.. items]);
        return Task.CompletedTask;
    }

    public void ResetInput()
    {
    }
}
