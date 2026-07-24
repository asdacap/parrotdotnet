using Parrot.Auth;

namespace Parrot.Core.Tests;

internal sealed class OAuthTokenSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_token_more_than_five_minutes_from_expiry_is_returned_unchanged(CancellationToken cancellationToken)
    {
        var store = new InMemoryCredentialStore();
        await store.Set("openai", Oauth(Now.AddMinutes(30), "fresh"), cancellationToken);
        var client = new FakeOAuthClient(Now);
        using var source = new OAuthTokenSource(store, client, "openai");

        var access = await source.Token(cancellationToken);

        _ = await Assert.That(access.AccessToken).IsEqualTo("fresh");
        _ = await Assert.That(client.Refreshes).IsEqualTo(0);
    }

    [Test]
    public async Task Concurrent_callers_near_expiry_trigger_one_refresh_that_is_persisted(CancellationToken cancellationToken)
    {
        var store = new InMemoryCredentialStore();
        await store.Set("openai", Oauth(Now.AddMinutes(1), "stale"), cancellationToken);
        var client = new FakeOAuthClient(Now);
        using var source = new OAuthTokenSource(store, client, "openai");

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => source.Token(cancellationToken)));

        _ = await Assert.That(client.Refreshes).IsEqualTo(1);
        _ = await Assert.That(results.Select(access => access.AccessToken).Distinct().Single()).IsEqualTo("rotated");
        var oauth = (await store.Get("openai", cancellationToken))?.OAuth;
        _ = await Assert.That(oauth?.RefreshToken.Value).IsEqualTo("rotated-refresh");
        _ = await Assert.That(oauth?.AccountId).IsEqualTo("acct");
    }

    private static Credential Oauth(DateTimeOffset expiresAt, string access) =>
        Credential.ForOAuth(new OAuthCredential
        {
            AccessToken = new Secret(access),
            RefreshToken = new Secret("refresh"),
            ExpiresAt = expiresAt,
            AccountId = "acct",
        });

    private sealed class FakeOAuthClient(DateTimeOffset now) : IOAuthClient
    {
        public int Refreshes { get; private set; }

        public DateTimeOffset Now() => now;

        public async Task<OAuthCredential> Refresh(OAuthCredential current, CancellationToken cancellationToken)
        {
            Refreshes++;
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);

            return new OAuthCredential
            {
                AccessToken = new Secret("rotated"),
                RefreshToken = new Secret("rotated-refresh"),
                ExpiresAt = now.AddHours(1),
                AccountId = current.AccountId,
            };
        }
    }
}
