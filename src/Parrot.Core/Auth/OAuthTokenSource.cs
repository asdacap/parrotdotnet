namespace Parrot.Auth;

// Hands a provider a usable OAuth access token, refreshing five minutes before
// expiry. Concurrent callers that arrive during a refresh share one in-flight
// refresh, and a rotated refresh token is persisted before the result is
// returned. Port of Go's auth.TokenSource.
internal sealed class OAuthTokenSource(ICredentialStore store, IOAuthClient client, string name)
    : IOAuthTokenSource, IDisposable
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<OAuthCredential>? _flight;

    public void Dispose() => _gate.Dispose();

    public async Task<OAuthAccess> Token(CancellationToken cancellationToken)
    {
        var current = await ReadOAuth(cancellationToken).ConfigureAwait(false);

        if (current.ExpiresAt > client.Now().Add(RefreshMargin))
        {
            return Access(current);
        }

        Task<OAuthCredential> flight;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_flight is not null)
            {
                flight = _flight;
            }
            else
            {
                // A previous flight may have refreshed since the first read.
                var latest = await ReadOAuth(cancellationToken).ConfigureAwait(false);

                if (latest.ExpiresAt > client.Now().Add(RefreshMargin))
                {
                    return Access(latest);
                }

                flight = RefreshAndStore(latest, cancellationToken);
                _flight = flight;
            }
        }
        finally
        {
            _ = _gate.Release();
        }

        return Access(await flight.ConfigureAwait(false));
    }

    private static OAuthAccess Access(OAuthCredential credential) =>
        new(credential.AccessToken.Value, credential.AccountId);

    private async Task<OAuthCredential> RefreshAndStore(OAuthCredential current, CancellationToken cancellationToken)
    {
        try
        {
            var refreshed = await client.Refresh(current, cancellationToken).ConfigureAwait(false);
            await store.Set(name, Credential.ForOAuth(refreshed), cancellationToken).ConfigureAwait(false);
            return refreshed;
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _flight = null;
            _ = _gate.Release();
        }
    }

    private async Task<OAuthCredential> ReadOAuth(CancellationToken cancellationToken)
    {
        var credential = await store.Get(name, cancellationToken).ConfigureAwait(false);

        if (credential is null || credential.Type != CredentialType.OAuth || credential.OAuth is null)
        {
            throw new AuthException("auth: credential is not OAuth");
        }

        return credential.OAuth;
    }
}
