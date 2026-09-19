using System.Globalization;
using System.Text;
using Parrot.Cli.Commands;

namespace Parrot.Cli;

internal sealed class BasicSlashDialog(TextReader input, TextWriter output, TextWriter error) : ISlashDialog
{
    public async Task<SlashDialogOption?> Select(
        string title, IReadOnlyList<SlashDialogOption> options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(options);

        await output.WriteLineAsync(title.AsMemory(), cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            await output.WriteLineAsync(
                $"  {index + 1}. {option.Label} — {option.Description}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            await output.WriteAsync("> ".AsMemory(), cancellationToken).ConfigureAwait(false);
            var answer = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(answer))
            {
                return null;
            }

            var selected = Find(options, answer.Trim());
            if (selected is not null)
            {
                return selected;
            }

            await ShowError("Choose a listed number or id.", cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string?> ReadText(string prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        await output.WriteLineAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync("> ".AsMemory(), cancellationToken).ConfigureAwait(false);
        return await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadSecret(string prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(input, Console.In))
        {
            await output.WriteLineAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync("> ".AsMemory(), cancellationToken).ConfigureAwait(false);
            return await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ReadSecretFromConsole(prompt, cancellationToken).ConfigureAwait(false);
    }

    public async Task Show(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);

        foreach (var line in lines)
        {
            await output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task Print(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        await Show(lines, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> Confirm(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        await Show(lines, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Continue? [y/N]".AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync("> ".AsMemory(), cancellationToken).ConfigureAwait(false);
        var answer = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    public async Task ShowError(string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await error.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public Task<T> Load<T>(string activity, Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(load);
        return load(cancellationToken);
    }

    private static SlashDialogOption? Find(IReadOnlyList<SlashDialogOption> options, string answer)
    {
        if (int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number > 0 && number <= options.Count)
        {
            return options[number - 1];
        }

        return options.FirstOrDefault(option => option.Match(answer));
    }

    private async Task<string?> ReadSecretFromConsole(string prompt, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync("> ".AsMemory(), cancellationToken).ConfigureAwait(false);
        var typed = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                await output.WriteLineAsync().ConfigureAwait(false);
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Escape || (key.Key == ConsoleKey.C &&
                key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                await output.WriteLineAsync().ConfigureAwait(false);
                return null;
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
