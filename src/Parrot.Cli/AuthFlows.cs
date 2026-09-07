using Parrot.Auth;
using Parrot.Llm;

namespace Parrot.Cli;

// The credential-login flows shared by the `auth` subcommand and the `/auth`
// slash command: store an API key, or run one of the ChatGPT OAuth flows.
internal static class AuthFlows
{
    public static async Task StoreApiKey(
        ICredentialStore store, string providerId, string key, CancellationToken cancellationToken) =>
        await store.Set(providerId, Credential.ForApiKey(key), cancellationToken).ConfigureAwait(false);

    public static async Task OAuthLogin(
        IOAuthClient oauth,
        ICredentialStore store,
        bool device,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        OAuthCredential credential;

        if (device)
        {
            var authorization = await oauth.StartDeviceAuthorization(cancellationToken).ConfigureAwait(false);

            // The user must see the code, so this is the one place a Secret's
            // real value is deliberately printed.
            await output.WriteLineAsync(
                $"  visit {authorization.VerificationUrl} and enter code {authorization.UserCode.Value}".AsMemory(),
                cancellationToken).ConfigureAwait(false);

            credential = await oauth.AwaitDeviceAuthorization(authorization, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync("  opening your browser to authorize...".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            credential = await oauth.BrowserLogin(cancellationToken).ConfigureAwait(false);
        }

        await store.Set(ChatGptProvider.ProviderId, Credential.ForOAuth(credential), cancellationToken)
            .ConfigureAwait(false);
    }
}
