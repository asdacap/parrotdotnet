using System.Threading.Channels;

namespace Parrot.Cli.Tests;

// A keyboard the test types on, one line at a time and whenever it likes. The
// timing is the point: a line typed after the turn has started is the case the
// old driver could not read at all.
internal sealed class ScriptedInput : TextReader
{
    private readonly Channel<string> _typed = Channel.CreateUnbounded<string>();

    public void Type(string line) => _typed.Writer.TryWrite(line);

    // End of input, which is how the loop is told to leave.
    public void End() => _typed.Writer.TryComplete();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (await _typed.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_typed.Reader.TryRead(out var line))
            {
                return line;
            }
        }

        return null;
    }
}
