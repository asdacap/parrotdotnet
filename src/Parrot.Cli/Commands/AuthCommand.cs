namespace Parrot.Cli.Commands;

// Authenticating without leaving the session is the point: a fresh install can
// reach a working prompt without knowing the subcommand form exists.
internal sealed class AuthCommand(Func<string> readSecret) : ISlashCommand
{
    public string Name => "/auth";

    public string Summary => "Store a provider key (login)";

    public async Task<SlashOutcome> Run(
        SlashContext context, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(arguments, "login", StringComparison.Ordinal))
        {
            await context.Output.WriteLineAsync("usage: /auth login".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        await context.Output
            .WriteAsync($"  key for {context.ProviderId}: ".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        var key = readSecret().Trim();
        await context.Output.WriteLineAsync().ConfigureAwait(false);

        if (key.Length == 0)
        {
            await context.Error.WriteLineAsync("  nothing entered".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        await context.Credentials.Set(context.ProviderId, key, cancellationToken).ConfigureAwait(false);

        await context.Output
            .WriteLineAsync($"  stored a credential for {context.ProviderId}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return SlashOutcome.Continue;
    }
}
