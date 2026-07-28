namespace Parrot.Cli.Commands;

// What a command needs from whoever owns the terminal. The basic CLI can lean on the
// kernel's line editing; the enhanced one holds the terminal in raw mode and has to do
// its own, so neither can be assumed.
internal interface IPromptReader
{
    Task<string?> ReadLine(CancellationToken cancellationToken);

    Task<string> ReadSecret(CancellationToken cancellationToken);
}
