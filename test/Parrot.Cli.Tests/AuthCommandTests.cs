using Parrot.Auth;
using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class AuthCommandTests
{
    [Test]
    public async Task Api_key_login_list_and_logout_follow_the_wizard_without_leaking_the_secret(
        CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var directory = Path.Combine(Path.GetTempPath(), $"parrot-credentials-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "credentials.json");
        try
        {
            using var credentialStoreOwner = new FileCredentialStore(path);
            ICredentialStore credentials = credentialStoreOwner;
            using var http = new HttpClient();
            IOAuthClient oauth = new OpenAiOAuthClient(http, new UnusedBrowser(), new OpenAiOAuthOptions());
            var login = new TestSlashDialog().Select("login", "provider").Secret("  private-key  ");
            var list = new TestSlashDialog().Select("list");
            var logout = new TestSlashDialog().Select("logout", "provider");
            ISlashCommand command = new AuthCommand(credentials, oauth, ["provider"], login, diagnostics.Log);
            ISlashCommand listCommand = new AuthCommand(credentials, oauth, ["provider"], list, diagnostics.Log);
            ISlashCommand logoutCommand = new AuthCommand(credentials, oauth, ["provider"], logout, diagnostics.Log);

            await command.Run(string.Empty, cancellationToken);
            _ = await Assert.That(await credentials.Get("provider", cancellationToken)).IsNotNull();
            await listCommand.Run(string.Empty, cancellationToken);
            await logoutCommand.Run(string.Empty, cancellationToken);

            _ = await Assert.That(string.Join('|', login.Shown)).Contains("stored a credential for provider");
            _ = await Assert.That(string.Join('|', login.Shown)).DoesNotContain("private-key");
            _ = await Assert.That(string.Join('|', list.Shown)).Contains("provider");
            _ = await Assert.That(await credentials.Get("provider", cancellationToken)).IsNull();
            var log = diagnostics.Read();
            _ = await Assert.That(log).Contains("login_start").And.Contains("login_complete")
                .And.Contains("list_start").And.Contains("list_complete")
                .And.Contains("logout_start").And.Contains("logout_complete")
                .And.Contains("duration_ms").And.Contains("correlation=")
                .And.DoesNotContain("private-key").And.DoesNotContain("provider");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    [Arguments("interactive", "dismissed")]
    [Arguments("login", "dismissed")]
    [Arguments("oauth", "dismissed")]
    [Arguments("empty_input", "empty_input")]
    [Arguments("no_providers", "no_providers")]
    public async Task Dismissed_and_empty_wizards_report_the_actual_outcome(
        string stage, string outcome, CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var dialog = stage switch
        {
            "interactive" => new TestSlashDialog().Select((string?)null),
            "login" => new TestSlashDialog().Select("login", null),
            "oauth" => new TestSlashDialog().Select("login", "chatgpt", null),
            "empty_input" => new TestSlashDialog().Select("login", "sentinel-provider").Secret(" "),
            _ => new TestSlashDialog().Select("login"),
        };
        ISlashCommand command = new AuthCommand(
            new UnusedCredentials(),
            new DiagnosticOAuthClient(false),
            stage == "no_providers" ? [] : ["sentinel-provider", "chatgpt"],
            dialog,
            diagnostics.Log);

        await command.Run("sentinel-argument", cancellationToken);

        var text = diagnostics.Read();
        _ = await Assert.That(text).Contains("interactive_start").And.Contains("interactive_complete")
            .And.Contains("outcome=\"" + outcome + "\"").And.Contains("duration_ms=").And.DoesNotContain("sentinel");
        if (stage is "login" or "oauth")
        {
            _ = await Assert.That(text).Contains(stage + "_start").And.Contains(stage + "_complete");
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task OAuth_reports_swallowed_auth_failures_and_success_without_sensitive_details(
        bool device, bool fail, CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var directory = Path.Combine(Path.GetTempPath(), $"parrot-slash-auth-{Guid.NewGuid():N}");
        try
        {
            using var credentials = new FileCredentialStore(Path.Combine(directory, "credentials.json"));
            var dialog = new TestSlashDialog().Select("login", "chatgpt", device ? "device" : "browser");
            ISlashCommand command = new AuthCommand(
                credentials, new DiagnosticOAuthClient(fail), ["chatgpt"], dialog, diagnostics.Log);

            await command.Run("sentinel-argument", cancellationToken);

            var text = diagnostics.Read();
            _ = await Assert.That(text).Contains("oauth_start").And.Contains("oauth_complete")
                .And.Contains(fail ? "outcome=\"auth_failure\"" : "outcome=\"completed\"")
                .And.DoesNotContain("sentinel").And.DoesNotContain("chatgpt");
            _ = await Assert.That(dialog.Errors.Count).IsEqualTo(fail ? 1 : 0);
            if (fail)
            {
                _ = await Assert.That(dialog.Errors[0]).IsEqualTo("sentinel-failure");
                _ = await Assert.That(text).Contains("error=\"auth\"");
            }
            else
            {
                _ = await Assert.That(await credentials.Get("chatgpt", cancellationToken)).IsNotNull();
            }

            if (device)
            {
                _ = await Assert.That(string.Join('|', dialog.Shown)).Contains("sentinel-code");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Propagated_failures_and_cancellation_are_classified(bool cancel, CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var dialog = new TestSlashDialog().Select("list");
        ISlashCommand command = new AuthCommand(
            new UnusedCredentials(), new DiagnosticOAuthClient(false), [], dialog, diagnostics.Log);
        if (cancel)
        {
            await stopping.CancelAsync();
            _ = await Assert.That(async () => await command.Run("sentinel-argument", stopping.Token))
                .Throws<OperationCanceledException>();
        }
        else
        {
            _ = await Assert.That(async () => await command.Run("sentinel-argument", stopping.Token))
                .Throws<NotSupportedException>();
        }

        var text = diagnostics.Read();
        _ = await Assert.That(text).Contains("interactive_failure")
            .And.Contains(cancel ? "error=\"cancelled\"" : "error=\"unexpected\"")
            .And.DoesNotContain("sentinel").And.DoesNotContain("driving the loop");
        if (!cancel)
        {
            _ = await Assert.That(text).Contains("list_failure");
        }
    }

    private sealed class DiagnosticOAuthClient(bool fail) : IOAuthClient
    {
        public string AuthorizationUrl(string redirect, string challenge, string state) =>
            throw new NotSupportedException();

        public Task<OAuthCredential> BrowserLogin(CancellationToken cancellationToken) => CompleteLogin();

        public Task<DeviceAuthorization> StartDeviceAuthorization(CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceAuthorization
            {
                VerificationUrl = "https://sentinel.invalid/?secret=sentinel-query",
                UserCode = new Secret("sentinel-code"),
            });

        public Task<OAuthCredential> AwaitDeviceAuthorization(DeviceAuthorization device, CancellationToken cancellationToken) =>
            CompleteLogin();

        public Task<OAuthCredential> Refresh(OAuthCredential current, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public DateTimeOffset Now() => throw new NotSupportedException();

        private Task<OAuthCredential> CompleteLogin() => fail
            ? Task.FromException<OAuthCredential>(new AuthException("sentinel-failure"))
            : Task.FromResult(OAuthCredential.Create("sentinel-access", "sentinel-refresh", DateTimeOffset.MaxValue, "sentinel-account"));
    }
}
