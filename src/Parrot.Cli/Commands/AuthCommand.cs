using Parrot.Auth;
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
            await context.Output.WriteLineAsync("usage: /auth login [provider] [--device]".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        var provider = tokens.Skip(1).FirstOrDefault(token => !token.StartsWith('-'));
        var device = tokens.Skip(1).Contains("--device", StringComparer.Ordinal);

        if (provider is null)
        {
            provider = await SelectProvider(context, cancellationToken).ConfigureAwait(false);

            if (provider is null)
            {
                return SlashOutcome.Continue;
            }
        }

        if (!context.ProviderIds.Contains(provider, StringComparer.Ordinal))
        {
            await WriteValidProviders(context, cancellationToken).ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        if (provider == ChatGptProvider.ProviderId)
        {
            try
            {
                await AuthFlows.OAuthLogin(context.OAuth, context.Credentials, device, context.Output, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AuthException failure)
            {
                await context.Error.WriteLineAsync($"  {failure.Message}".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                return SlashOutcome.Continue;
            }

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

    private static async Task<string?> SelectProvider(SlashContext context, CancellationToken cancellationToken)
    {
        await context.Output.WriteLineAsync("  providers:".AsMemory(), cancellationToken).ConfigureAwait(false);

        foreach (var providerId in context.ProviderIds)
        {
            await context.Output.WriteLineAsync($"    {providerId}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await context.Output.WriteAsync("  provider: ".AsMemory(), cancellationToken).ConfigureAwait(false);
        var selected = await context.Input.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        if (selected is not null && context.ProviderIds.Contains(selected.Trim(), StringComparer.Ordinal))
        {
            return selected.Trim();
        }

        await WriteValidProviders(context, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static Task WriteValidProviders(SlashContext context, CancellationToken cancellationToken) =>
        context.Error.WriteLineAsync(
            $"  choose one of: {string.Join(", ", context.ProviderIds)}".AsMemory(), cancellationToken);
}
