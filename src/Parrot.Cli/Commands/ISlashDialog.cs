namespace Parrot.Cli.Commands;

/// <summary>Provides interactive selection, input, and messages for slash commands.</summary>
internal interface ISlashDialog
{
    /// <summary>Returns the selected option, or null when selection is dismissed.</summary>
    Task<SlashDialogOption?> Select(
        string title, IReadOnlyList<SlashDialogOption> options, CancellationToken cancellationToken);

    /// <summary>Returns entered text, or null when input is dismissed.</summary>
    Task<string?> ReadText(string prompt, CancellationToken cancellationToken);

    /// <summary>Reads text without displaying its contents, or returns null when input is dismissed.</summary>
    Task<string?> ReadSecret(string prompt, CancellationToken cancellationToken);

    Task Show(IReadOnlyList<string> lines, CancellationToken cancellationToken);

    Task<bool> Confirm(IReadOnlyList<string> lines, CancellationToken cancellationToken);

    Task ShowError(string message, CancellationToken cancellationToken);

    Task<T> Load<T>(string activity, Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken);
}
