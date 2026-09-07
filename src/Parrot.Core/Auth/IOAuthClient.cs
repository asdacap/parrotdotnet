namespace Parrot.Auth;

// Performs interactive authorization and refreshes credentials using the same issuer and clock.
internal interface IOAuthClient
{
    // Builds the PKCE authorization URL without starting a login or opening a browser.
    string AuthorizationUrl(string redirect, string challenge, string state);

    // Opens the browser and waits for a verified loopback callback, releasing the listener on completion or failure.
    Task<OAuthCredential> BrowserLogin(CancellationToken cancellationToken);

    // Starts device authorization so the caller can display its user code before polling.
    Task<DeviceAuthorization> StartDeviceAuthorization(CancellationToken cancellationToken);

    // Polls the issued device authorization until completion, cancellation, or the login deadline.
    Task<OAuthCredential> AwaitDeviceAuthorization(DeviceAuthorization device, CancellationToken cancellationToken);

    // Exchanges the refresh token, retaining the existing account identity when the response omits it.
    Task<OAuthCredential> Refresh(OAuthCredential current, CancellationToken cancellationToken);

    DateTimeOffset Now();
}
