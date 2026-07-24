namespace Parrot.Auth;

// The refresh half of the OAuth client, as the token source depends on it. A
// seam so the token source's refresh lifecycle is testable without HTTP.
internal interface IOAuthClient
{
    Task<OAuthCredential> Refresh(OAuthCredential current, CancellationToken cancellationToken);

    DateTimeOffset Now();
}
