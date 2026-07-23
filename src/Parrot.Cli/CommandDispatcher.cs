namespace Parrot.Cli;

internal static class CommandDispatcher
{
    public const int ExitSuccess = 0;
    public const int ExitUsage = 2;

    private const string UsageText = """
        parrot - a coding agent that is not too much

        Usage:
          parrot <command> [flags]

        Commands:
          help       Print this message
          version    Print the build version

        This is the .NET Native AOT port of parrot-coder. Commands are added as
        their upstream components are migrated; see MIGRATION.md.
        """;

    // The single top-level Run: every other Run in the process is a descendant
    // of this call, and the process exits when it returns. Writers are injected
    // so that command output is assertable without redirecting process streams.
    public static async Task<int> Run(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var command = arguments.Count == 0 ? "help" : arguments[0];

        switch (command)
        {
            case "help" or "--help" or "-h":
                await output.WriteLineAsync(UsageText.AsMemory(), cancellationToken);
                return ExitSuccess;

            case "version" or "--version":
                await output.WriteLineAsync($"{BuildInfo.ProductName} {BuildInfo.Version}".AsMemory(), cancellationToken);
                return ExitSuccess;

            default:
                await error.WriteLineAsync($"parrot: unknown command \"{command}\"".AsMemory(), cancellationToken);
                await error.WriteLineAsync("Run \"parrot help\" for usage.".AsMemory(), cancellationToken);
                return ExitUsage;
        }
    }
}
