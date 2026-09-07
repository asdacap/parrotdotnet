using System.Diagnostics;
using Parrot.Auth;
using Parrot.Diagnostics;
using Parrot.Llm;

namespace Parrot.Cli.Commands;

internal sealed class AuthCommand(
    ICredentialStore credentials,
    IOAuthClient oauth,
    IReadOnlyList<string> providerIds,
    ISlashDialog dialog,
    IDiagnosticLog diagnostics) : ISlashCommand
{
    public string Name => "/auth";

    public string Summary => "Manage provider credentials";

    public Task Run(string arguments, CancellationToken cancellationToken) =>
        RunOperation("interactive", SelectAction, cancellationToken);

    private async Task<string> SelectAction(CancellationToken cancellationToken)
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
            return "dismissed";
        }

        if (action.Id == "list")
        {
            return await RunOperation("list", List, cancellationToken).ConfigureAwait(false);
        }
        else if (action.Id == "logout")
        {
            return await RunOperation("logout", Logout, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return await RunOperation("login", Login, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> Login(CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0)
        {
            await dialog.ShowError("no providers are configured", cancellationToken).ConfigureAwait(false);
            return "no_providers";
        }

        var provider = await dialog.Select(
            "Select a provider",
            [.. providerIds.Select(id => new SlashDialogOption(id, id, "Provider credential"))],
            cancellationToken).ConfigureAwait(false);

        if (provider is null)
        {
            return "dismissed";
        }

        if (provider.Id == ChatGptProvider.ProviderId)
        {
            return await RunOperation("oauth", OAuth, cancellationToken).ConfigureAwait(false);
        }

        var key = await dialog.ReadSecret($"API key for {provider.Id}", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            await dialog.ShowError("nothing entered", cancellationToken).ConfigureAwait(false);
            return "empty_input";
        }

        await credentials.Set(provider.Id, Credential.ForApiKey(key.Trim()), cancellationToken).ConfigureAwait(false);
        await dialog.Show([$"stored a credential for {provider.Id}"], cancellationToken).ConfigureAwait(false);
        return "completed";
    }

    private async Task<string> OAuth(CancellationToken cancellationToken)
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
            return "dismissed";
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
                    return "dismissed";
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
                    return "dismissed";
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
            return "completed";
        }
        catch (AuthException failure)
        {
            await dialog.ShowError(failure.Message, cancellationToken).ConfigureAwait(false);
            return "auth_failure";
        }
    }

    private async Task<string> List(CancellationToken cancellationToken)
    {
        var stored = await dialog.Load(
            "Loading credentials…",
            async token => await credentials.List(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        await dialog.Show(stored.Count == 0 ? ["no credentials are stored"] : stored, cancellationToken)
            .ConfigureAwait(false);
        return "completed";
    }

    private async Task<string> Logout(CancellationToken cancellationToken)
    {
        var stored = await dialog.Load(
            "Loading credentials…",
            async token => await credentials.List(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (stored.Count == 0)
        {
            await dialog.Show(["no credentials are stored"], cancellationToken).ConfigureAwait(false);
            return "empty";
        }

        var selected = await dialog.Select(
            "Remove a credential",
            [.. stored.Select(id => new SlashDialogOption(id, id, "Stored credential"))],
            cancellationToken).ConfigureAwait(false);

        if (selected is null)
        {
            return "dismissed";
        }

        await credentials.Delete(selected.Id, cancellationToken).ConfigureAwait(false);
        await dialog.Show([$"removed the credential for {selected.Id}"], cancellationToken).ConfigureAwait(false);
        return "completed";
    }

    private async Task<string> RunOperation(
        string operation, Func<CancellationToken, Task<string>> run, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var correlationId = Guid.NewGuid().ToString("N");
        diagnostics.Write(new DiagnosticEvent("auth", operation + "_start", DiagnosticSeverity.Information)
        {
            CorrelationId = correlationId,
        });
        try
        {
            var outcome = await run(cancellationToken).ConfigureAwait(false);
            diagnostics.Write(new DiagnosticEvent(
                "auth", operation + "_complete", outcome == "auth_failure" ? DiagnosticSeverity.Error : DiagnosticSeverity.Information)
            {
                CorrelationId = correlationId,
                Outcome = outcome,
                ErrorCode = outcome == "auth_failure" ? "auth" : null,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
            return outcome;
        }
        catch (Exception failure)
        {
            diagnostics.Write(new DiagnosticEvent(
                "auth", operation + "_failure", failure is OperationCanceledException ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
            {
                CorrelationId = correlationId,
                Outcome = failure is OperationCanceledException ? "cancelled" : "failed",
                ErrorCode = failure is AuthException ? "auth" : DiagnosticEvent.ClassifyFailure(failure),
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
            throw;
        }
    }
}
