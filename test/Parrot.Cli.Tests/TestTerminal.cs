using System.Text;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class TestTerminal(TextReader input, TextWriter output, TextWriter error, int columns) : ITerminal
{
    private int _columns = columns;

    public TextWriter Output { get; } = output;

    public TextWriter Error { get; } = error;

    public bool Color => false;

    public int GetColumns() => _columns;

    public void Resize(int resizedColumns) => _columns = resizedColumns;

    public async ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken)
    {
        var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            buffer[0] = 0x04;
            return 1;
        }

        var encoded = Encoding.UTF8.GetBytes(line + "\r");
        encoded.CopyTo(buffer, 0);
        return encoded.Length;
    }
}
