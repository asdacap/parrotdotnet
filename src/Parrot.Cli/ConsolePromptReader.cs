using System.Text;
using Parrot.Cli.Commands;

namespace Parrot.Cli;

// The basic CLI never leaves canonical mode, so the kernel and Console can do the
// line editing here.
internal sealed class ConsolePromptReader(TextReader input) : IPromptReader
{
    public Task<string?> ReadLine(CancellationToken cancellationToken) =>
        input.ReadLineAsync(cancellationToken).AsTask();

    // Not ReadLine: a key must not land in the terminal scrollback, nor in a
    // screen recording.
    public Task<string> ReadSecret(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var typed = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                return Task.FromResult(typed.ToString());
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    _ = typed.Remove(typed.Length - 1, 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                _ = typed.Append(key.KeyChar);
            }
        }
    }
}
