using Parrot.Llm;

namespace Parrot.Cli.Commands;

// Authenticating without leaving the session is the point: a fresh install can
// reach a working prompt without knowing the subcommand form exists. A provider
// argument selects which one; chatgpt runs the OAuth flow, everything else takes
// a key.
internal sealed class AuthCommand(Func<string> readSecret) : ISlashCommand
{
    public string Name => "/auth";

    public string Summary => "Store a provider key or run OAuth (login)";

    public async Task<SlashOutcome> Run(
        SlashContext context, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0 || tokens[0] != "login")
        {
            await context.Output.WriteLineAsync("usage: /auth login <provider> [--device]".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        var provider = tokens.Length > 1 && !tokens[1].StartsWith('-') ? tokens[1] : context.ProviderId;
        var device = arguments.Contains("--device", StringComparison.Ordinal);

        if (provider.Length == 0)
        {
            await context.Output.WriteLineAsync("  usage: /auth login <provider>".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        if (provider == ChatGptProvider.ProviderId)
        {
            await AuthFlows.OAuthLogin(context.OAuth, context.Credentials, device, context.Output, cancellationToken)
                .ConfigureAwait(false);
            await context.Output.WriteLineAsync($"  stored a credential for {provider}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        await context.Output
            .WriteAsync($"  key for {provider}: ".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        var key = readSecret().Trim();
        await context.Output.WriteLineAsync().ConfigureAwait(false);

        if (key.Length == 0)
        {
            await context.Error.WriteLineAsync("  nothing entered".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        await AuthFlows.StoreApiKey(context.Credentials, provider, key, cancellationToken).ConfigureAwait(false);

        await context.Output
            .WriteLineAsync($"  stored a credential for {provider}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return SlashOutcome.Continue;
    }
}
