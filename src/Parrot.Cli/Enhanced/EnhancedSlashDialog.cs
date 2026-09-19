using Parrot.Cli.Commands;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedSlashDialog(
    ILiveInputHost input,
    Func<IScrollbackItem, CancellationToken, Task> commit) : ISlashDialog
{
    private const int MaximumInputRunes = 4096;
    private const int MaximumVisibleMessageLines = 9;
    private const int MaximumVisibleOptions = 8;

    private static PromptValue CaretItem { get; } = new("> ", string.Empty, 0);

    public async Task<SlashDialogOption?> Select(
        string title,
        IReadOnlyList<SlashDialogOption> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var editor = new IncrementalEditor(title, MaximumInputRunes);
        var selected = 0;

        try
        {
            while (true)
            {
                var matches = Matches(options, editor.Prompt.Text);
                selected = matches.Count == 0 ? 0 : Math.Clamp(selected, 0, matches.Count - 1);
                cancellationToken.ThrowIfCancellationRequested();
                await input.ReplaceInput(PickerItems(editor.Prompt, matches, selected), cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var key = await input.ReadKey(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (IsCancellation(key))
                {
                    return null;
                }

                if (key.Kind == TerminalKeyKind.Submit)
                {
                    matches = Matches(options, editor.Prompt.Text);
                    if (matches.Count > 0)
                    {
                        var choice = matches[Math.Clamp(selected, 0, matches.Count - 1)];
                        cancellationToken.ThrowIfCancellationRequested();
                        await input.ReplaceInput(
                            [new LiveTextValue(title), new PromptValue("> ", choice.Label, choice.Label.EnumerateRunes().Count())],
                            cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        return choice;
                    }

                    continue;
                }

                if (key.Kind is TerminalKeyKind.Up or TerminalKeyKind.Down)
                {
                    matches = Matches(options, editor.Prompt.Text);
                    if (matches.Count > 0)
                    {
                        selected = key.Kind == TerminalKeyKind.Up
                            ? (selected - 1 + matches.Count) % matches.Count
                            : (selected + 1) % matches.Count;
                    }

                    continue;
                }

                if (key.Kind != TerminalKeyKind.Paste || !key.Text.Contains('\n', StringComparison.Ordinal))
                {
                    _ = editor.Apply(key);
                    selected = 0;
                }
            }
        }
        finally
        {
            input.ResetInput();
        }
    }

    public Task<string?> ReadText(string prompt, CancellationToken cancellationToken) =>
        Read(prompt, false, cancellationToken);

    public Task<string?> ReadSecret(string prompt, CancellationToken cancellationToken) =>
        Read(prompt, true, cancellationToken);

    public async Task Show(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _ = await ShowMessage(string.Join('\n', lines), false, cancellationToken).ConfigureAwait(false);
    }

    public Task Print(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return commit(ImmediateScrollbackValue.Muted(lines), cancellationToken);
    }

    public Task<bool> Confirm(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return ShowMessage(string.Join('\n', lines), false, cancellationToken);
    }

    public async Task ShowError(string message, CancellationToken cancellationToken) =>
        _ = await ShowMessage(message, true, cancellationToken).ConfigureAwait(false);

    public async Task<T> Load<T>(
        string activity,
        Func<CancellationToken, Task<T>> load,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(load);

        var pending = load(cancellationToken);
        var spinner = new TerminalSpinner((items, token) =>
            input.ReplaceInput([CaretItem, .. items], token));
        await spinner.Run(
            index => new SpinnerValue(activity, index),
            (_, token) => pending.WaitAsync(token),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await pending.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static bool IsCancellation(TerminalKey key) =>
        key.Kind is TerminalKeyKind.Escape or TerminalKeyKind.Interrupt or TerminalKeyKind.EndOfFile;

    private static List<SlashDialogOption> Matches(IReadOnlyList<SlashDialogOption> options, string query) =>
        [.. options.Where(option =>
            option.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || option.Description.Contains(query, StringComparison.OrdinalIgnoreCase))];

    private static List<ILiveBufferItem> PickerItems(
        PromptState prompt,
        List<SlashDialogOption> matches,
        int selected)
    {
        var start = Math.Clamp(
            selected - MaximumVisibleOptions + 1,
            0,
            Math.Max(0, matches.Count - MaximumVisibleOptions));
        List<ILiveBufferItem> items = [new LiveTextValue(prompt.Prefix), new PromptValue(prompt with { Prefix = "> " })];
        if (matches.Count == 0)
        {
            items.Add(new PickerOptionValue("No matches", string.Empty, false));
            return items;
        }

        items.AddRange(matches
            .Skip(start)
            .Take(MaximumVisibleOptions)
            .Select((option, index) => (ILiveBufferItem)new PickerOptionValue(
                option.Label,
                option.Description,
                start + index == selected)));
        return items;
    }

    private static PromptValue InputItem(PromptState prompt, bool secret) => secret
        ? new PromptValue("> ", new string('*', prompt.Text.EnumerateRunes().Count()), prompt.Cursor)
        : new PromptValue("> ", prompt.Text, prompt.Cursor);

    private async Task<bool> ShowMessage(string message, bool error, CancellationToken cancellationToken)
    {
        var lines = message.Split('\n');
        var offset = 0;
        while (true)
        {
            var visible = string.Join('\n', lines.Skip(offset).Take(MaximumVisibleMessageLines));
            await input.ReplaceInput([new DialogMessageValue(visible, error)], cancellationToken).ConfigureAwait(false);
            var key = await input.ReadKey(cancellationToken).ConfigureAwait(false);
            if (key.Kind == TerminalKeyKind.Up)
            {
                offset = Math.Max(0, offset - 1);
            }
            else if (key.Kind == TerminalKeyKind.Down)
            {
                offset = Math.Min(Math.Max(0, lines.Length - MaximumVisibleMessageLines), offset + 1);
            }
            else
            {
                return !IsCancellation(key);
            }
        }
    }

    private async Task<string?> Read(string prompt, bool secret, CancellationToken cancellationToken)
    {
        var editor = new IncrementalEditor(prompt, MaximumInputRunes);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await input.ReplaceInput(
                    [new LiveTextValue(prompt), InputItem(editor.Prompt, secret)],
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var key = await input.ReadKey(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (IsCancellation(key))
                {
                    return null;
                }

                if (key.Kind == TerminalKeyKind.Submit)
                {
                    var submitted = editor.Prompt.Text;
                    var completed = new PromptState(prompt, submitted, submitted.EnumerateRunes().Count());
                    cancellationToken.ThrowIfCancellationRequested();
                    await input.ReplaceInput(
                        [new LiveTextValue(prompt), InputItem(completed, secret)],
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return submitted;
                }

                _ = editor.Apply(key);
            }
        }
        finally
        {
            input.ResetInput();
        }
    }
}
