namespace Parrot.Auth;

// A provider's outbound dependency for OAuth credentials: hands back a valid
// access token, refreshing transparently when the stored one is near expiry.
// The provider owns refresh through this seam, per architecture principle 4.
internal interface IOAuthTokenSource
{
    Task<OAuthAccess> Token(CancellationToken cancellationToken);
}
