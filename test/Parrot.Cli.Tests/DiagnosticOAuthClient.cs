using Parrot.Auth;

namespace Parrot.Cli.Tests;

internal sealed class DiagnosticOAuthClient(Exception? failure) : IOAuthClient
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

    private Task<OAuthCredential> CompleteLogin() => failure is null
        ? Task.FromResult(OAuthCredential.Create("sentinel-access", "sentinel-refresh", DateTimeOffset.MaxValue, "sentinel-account"))
        : Task.FromException<OAuthCredential>(failure);
}
