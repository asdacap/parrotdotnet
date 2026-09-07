namespace Parrot.Cli.Commands;

internal interface ISlashDialog
{
    Task<SlashDialogOption?> Select(
        string title, IReadOnlyList<SlashDialogOption> options, CancellationToken cancellationToken);

    Task<string?> ReadText(string prompt, CancellationToken cancellationToken);

    Task<string?> ReadSecret(string prompt, CancellationToken cancellationToken);

    Task Show(IReadOnlyList<string> lines, CancellationToken cancellationToken);

    Task<bool> Confirm(IReadOnlyList<string> lines, CancellationToken cancellationToken);

    Task ShowError(string message, CancellationToken cancellationToken);

    Task<T> Load<T>(string activity, Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken);
}
