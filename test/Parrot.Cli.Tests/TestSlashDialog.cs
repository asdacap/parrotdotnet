using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class TestSlashDialog : ISlashDialog
{
    private readonly Queue<string?> _secrets = [];
    private readonly Queue<string?> _selections = [];

    public List<string> Shown { get; } = [];

    public List<string> Errors { get; } = [];

    public List<(string Title, IReadOnlyList<SlashDialogOption> Options)> Pickers { get; } = [];

    public TestSlashDialog Select(params string?[] ids)
    {
        foreach (var id in ids)
        {
            _selections.Enqueue(id);
        }

        return this;
    }

    public TestSlashDialog Secret(params string?[] values)
    {
        foreach (var value in values)
        {
            _secrets.Enqueue(value);
        }

        return this;
    }

    public Task<SlashDialogOption?> Select(
        string title, IReadOnlyList<SlashDialogOption> options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pickers.Add((title, options));
        var id = _selections.Dequeue();
        return Task.FromResult(id is null
            ? null
            : options.Single(option => string.Equals(option.Id, id, StringComparison.Ordinal)));
    }

    public Task<string?> ReadText(string prompt, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<string?> ReadSecret(string prompt, CancellationToken cancellationToken) =>
        Task.FromResult(_secrets.Count == 0 ? null : _secrets.Dequeue());

    public Task Show(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        Shown.AddRange(lines);
        return Task.CompletedTask;
    }

    public Task<bool> Confirm(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        Shown.AddRange(lines);
        return Task.FromResult(true);
    }

    public Task ShowError(string message, CancellationToken cancellationToken)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }
}
