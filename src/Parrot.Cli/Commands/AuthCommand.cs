using Parrot.Auth;
using Parrot.Llm;

namespace Parrot.Cli.Commands;

internal sealed class AuthCommand(
    ICredentialStore credentials,
    IOAuthClient oauth,
    IReadOnlyList<string> providerIds,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/auth";

    public string Summary => "Manage provider credentials";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        var action = await dialog.Select(
            "Authentication",
            [
                new("login", "Login", "Store a provider credential"),
                new("list", "List", "List stored credentials"),
                new("logout", "Logout", "Remove a stored credential"),
            ],
            cancellationToken).ConfigureAwait(false);

        if (action is null)
        {
            return;
        }

        if (action.Id == "list")
        {
            await List(cancellationToken).ConfigureAwait(false);
        }
        else if (action.Id == "logout")
        {
            await Logout(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Login(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task Login(CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0)
        {
            await dialog.ShowError("no providers are configured", cancellationToken).ConfigureAwait(false);
            return;
        }

        var provider = await dialog.Select(
            "Select a provider",
            [.. providerIds.Select(id => new SlashDialogOption(id, id, "Provider credential"))],
            cancellationToken).ConfigureAwait(false);

        if (provider is null)
        {
            return;
        }

        if (provider.Id == ChatGptProvider.ProviderId)
        {
            await OAuth(cancellationToken).ConfigureAwait(false);
            return;
        }

        var key = await dialog.ReadSecret($"API key for {provider.Id}", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            await dialog.ShowError("nothing entered", cancellationToken).ConfigureAwait(false);
            return;
        }

        await credentials.Set(provider.Id, Credential.ForApiKey(key.Trim()), cancellationToken).ConfigureAwait(false);
        await dialog.Show([$"stored a credential for {provider.Id}"], cancellationToken).ConfigureAwait(false);
    }

    private async Task OAuth(CancellationToken cancellationToken)
    {
        var method = await dialog.Select(
            "Sign in to ChatGPT",
            [
                new("browser", "Browser", "Open a browser to authorize"),
                new("device", "Device code", "Authorize on another device"),
            ],
            cancellationToken).ConfigureAwait(false);

        if (method is null)
        {
            return;
        }

        try
        {
            OAuthCredential credential;
            if (method.Id == "device")
            {
                var authorization = await oauth.StartDeviceAuthorization(cancellationToken).ConfigureAwait(false);
                var proceed = await dialog.Confirm(
                    [$"Visit {authorization.VerificationUrl} and enter code {authorization.UserCode.Value}"],
                    cancellationToken).ConfigureAwait(false);
                if (!proceed)
                {
                    return;
                }

                credential = await dialog.Load(
                    "Waiting for authorization…",
                    token => oauth.AwaitDeviceAuthorization(authorization, token),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var proceed = await dialog.Confirm(
                    ["Opening your browser to authorize..."], cancellationToken).ConfigureAwait(false);
                if (!proceed)
                {
                    return;
                }

                credential = await dialog.Load(
                    "Waiting for browser authorization…",
                    oauth.BrowserLogin,
                    cancellationToken).ConfigureAwait(false);
            }

            await credentials.Set(ChatGptProvider.ProviderId, Credential.ForOAuth(credential), cancellationToken)
                .ConfigureAwait(false);
            await dialog.Show(
                [$"stored a credential for {ChatGptProvider.ProviderId}"], cancellationToken).ConfigureAwait(false);
        }
        catch (AuthException failure)
        {
            await dialog.ShowError(failure.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task List(CancellationToken cancellationToken)
    {
        var stored = await dialog.Load(
            "Loading credentials…",
            async token => await credentials.List(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        await dialog.Show(stored.Count == 0 ? ["no credentials are stored"] : stored, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task Logout(CancellationToken cancellationToken)
    {
        var stored = await dialog.Load(
            "Loading credentials…",
            async token => await credentials.List(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (stored.Count == 0)
        {
            await dialog.Show(["no credentials are stored"], cancellationToken).ConfigureAwait(false);
            return;
        }

        var selected = await dialog.Select(
            "Remove a credential",
            [.. stored.Select(id => new SlashDialogOption(id, id, "Stored credential"))],
            cancellationToken).ConfigureAwait(false);

        if (selected is null)
        {
            return;
        }

        await credentials.Delete(selected.Id, cancellationToken).ConfigureAwait(false);
        await dialog.Show([$"removed the credential for {selected.Id}"], cancellationToken).ConfigureAwait(false);
    }
}
