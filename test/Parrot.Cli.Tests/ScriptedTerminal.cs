using System.Text;
using System.Threading.Channels;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class ScriptedTerminal(int columns) : ITerminal, IDisposable
{
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();

    public TextWriter Output { get; } = new StringWriter();

    public TextWriter Error { get; } = new StringWriter();

    public bool Color => false;

    public int GetColumns() => columns;

    public void Type(string value) => _input.Writer.TryWrite(Encoding.UTF8.GetBytes(value));

    public void Tick() => _input.Writer.TryWrite([]);

    public void End() => _input.Writer.TryWrite([0x04]);

    public async ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken)
    {
        var input = await _input.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        input.CopyTo(buffer, 0);
        return input.Length;
    }

    public void Dispose()
    {
        _ = _input.Writer.TryComplete();
        Output.Dispose();
        Error.Dispose();
    }
}
