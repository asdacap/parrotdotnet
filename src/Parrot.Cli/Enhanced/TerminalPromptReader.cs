using System.Text;
using Parrot.Cli.Commands;

namespace Parrot.Cli.Enhanced;

// Console.In and Console.ReadKey would both drag StdInReader in: it takes the terminal
// out of the raw mode this CLI depends on and never puts it back, and it echoes in its
// own colours. Decoding keys off the raw terminal is what keeps the prompt ours.
internal sealed class TerminalPromptReader(ITerminal terminal) : IPromptReader
{
    public Task<string?> ReadLine(CancellationToken cancellationToken) => Read(true, cancellationToken);

    public async Task<string> ReadSecret(CancellationToken cancellationToken) =>
        await Read(false, cancellationToken).ConfigureAwait(false) ?? string.Empty;

    private async Task<string?> Read(bool echo, CancellationToken cancellationToken)
    {
        var decoder = new TerminalKeyDecoder();
        var buffer = new byte[1024];
        var typed = new StringBuilder();

        while (true)
        {
            var count = await terminal.Read(buffer, cancellationToken).ConfigureAwait(false);

            // A zero count is the VMIN=0/VTIME=1 poll tick, not end of input.
            foreach (var key in count == 0 ? decoder.Flush() : decoder.Feed(buffer.AsSpan(0, count)))
            {
                if (key.Kind == TerminalKeyKind.Submit)
                {
                    return typed.ToString();
                }

                if (key.Kind is TerminalKeyKind.EndOfFile or TerminalKeyKind.Interrupt)
                {
                    return null;
                }

                if (key.Kind == TerminalKeyKind.Backspace && typed.Length > 0)
                {
                    _ = typed.Remove(typed.Length - 1, 1);
                    await Echo(echo, "\b \b", cancellationToken).ConfigureAwait(false);
                }
                else if (key.Kind == TerminalKeyKind.Character)
                {
                    _ = typed.Append(key.Text);
                    await Echo(echo, key.Text, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private Task Echo(bool echo, string text, CancellationToken cancellationToken) =>
        echo ? terminal.Output.WriteAsync(text.AsMemory(), cancellationToken) : Task.CompletedTask;
}
