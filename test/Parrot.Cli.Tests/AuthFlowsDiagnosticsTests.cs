using Parrot.Auth;
using Parrot.Diagnostics;
using Parrot.State;

namespace Parrot.Cli.Tests;

internal sealed class AuthFlowsDiagnosticsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Api_key_outcomes_are_logged_without_credentials_or_failure_text(
        bool fail, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"parrot-auth-diagnostics-{Guid.NewGuid():N}");
        var paths = new StatePaths(directory, directory, directory);
        try
        {
            using var diagnostics = new DiagnosticLogs(paths, "auth-test", TextWriter.Null, TimeProvider.System);
            using var storeOwner = new FileCredentialStore(Path.Combine(directory, "credentials.json"));
            ICredentialStore store = fail ? new UnusedCredentials() : storeOwner;
            if (fail)
            {
                _ = await Assert.That(async () => await AuthFlows.StoreApiKey(
                    store, "provider-secret", "credential-secret", diagnostics.Global, cancellationToken))
                    .Throws<NotSupportedException>();
            }
            else
            {
                await AuthFlows.StoreApiKey(store, "provider-secret", "credential-secret", diagnostics.Global, cancellationToken);
                _ = await Assert.That(await store.Get("provider-secret", cancellationToken)).IsNotNull();
            }

            var text = await File.ReadAllTextAsync(Directory.GetFiles(paths.LogDirectory).Single(), cancellationToken);
            _ = await Assert.That(text).Contains("api_key_start");
            _ = await Assert.That(text).Contains(fail ? "api_key_failure" : "api_key_complete");
            _ = await Assert.That(text).Contains("duration_ms=");
            _ = await Assert.That(text).DoesNotContain("credential-secret");
            _ = await Assert.That(text).DoesNotContain("provider-secret");
            _ = await Assert.That(text).DoesNotContain("driving the loop stores no credential");
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
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task OAuth_outcomes_preserve_the_flow_without_logging_secrets(
        bool device, bool fail, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"parrot-oauth-diagnostics-{Guid.NewGuid():N}");
        var paths = new StatePaths(directory, directory, directory);
        try
        {
            using var diagnostics = new DiagnosticLogs(paths, "oauth-test", TextWriter.Null, TimeProvider.System);
            using var credentials = new FileCredentialStore(Path.Combine(directory, "credentials.json"));
            using var output = new StringWriter();
            IOAuthClient oauth = new DiagnosticOAuthClient(fail);
            if (fail)
            {
                _ = await Assert.That(async () => await AuthFlows.OAuthLogin(
                    oauth, credentials, device, output, diagnostics.Global, cancellationToken)).Throws<InvalidOperationException>();
            }
            else
            {
                await AuthFlows.OAuthLogin(oauth, credentials, device, output, diagnostics.Global, cancellationToken);
                _ = await Assert.That(await credentials.Get("chatgpt", cancellationToken)).IsNotNull();
            }

            var text = await File.ReadAllTextAsync(Directory.GetFiles(paths.LogDirectory).Single(), cancellationToken);
            _ = await Assert.That(text).Contains("oauth_start");
            _ = await Assert.That(text).Contains(fail ? "oauth_failure" : "oauth_complete");
            _ = await Assert.That(text).Contains("duration_ms=");
            _ = await Assert.That(text).DoesNotContain("sentinel");
            if (device)
            {
                _ = await Assert.That(output.ToString()).Contains("sentinel-code");
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
            ? Task.FromException<OAuthCredential>(new InvalidOperationException("sentinel-failure"))
            : Task.FromResult(OAuthCredential.Create("sentinel-access", "sentinel-refresh", DateTimeOffset.MaxValue, "sentinel-account"));
    }
}
