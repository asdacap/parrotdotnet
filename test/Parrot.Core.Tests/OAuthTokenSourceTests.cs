using Parrot.Auth;

namespace Parrot.Core.Tests;

internal sealed class OAuthTokenSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_token_more_than_five_minutes_from_expiry_is_returned_unchanged(CancellationToken cancellationToken)
    {
        ICredentialStore store = new InMemoryCredentialStore();
        await store.Set(
            "openai",
            Credential.ForOAuth(new OAuthCredential
            {
                AccessToken = new Secret("fresh"),
                RefreshToken = new Secret("refresh"),
                ExpiresAt = Now.AddMinutes(30),
                AccountId = "acct",
            }),
            cancellationToken);
        var client = new FakeOAuthClient(Now);
        using var sourceOwner = new OAuthTokenSource(store, client, "openai");
        IOAuthTokenSource source = sourceOwner;

        var access = await source.Token(cancellationToken);

        _ = await Assert.That(access.AccessToken).IsEqualTo("fresh");
        _ = await Assert.That(client.Refreshes).IsEqualTo(0);
    }

    [Test]
    public async Task Availability_accepts_expired_structurally_valid_oauth_without_refreshing(
        CancellationToken cancellationToken)
    {
        ICredentialStore store = new InMemoryCredentialStore();
        await store.Set(
            "openai",
            Credential.ForOAuth(new OAuthCredential
            {
                AccessToken = new Secret("expired"),
                RefreshToken = new Secret("refresh"),
                ExpiresAt = Now.AddMinutes(-1),
                AccountId = "acct",
            }),
            cancellationToken);
        var client = new FakeOAuthClient(Now);
        using var sourceOwner = new OAuthTokenSource(store, client, "openai");
        IOAuthTokenSource source = sourceOwner;

        var available = await source.HasCredential(cancellationToken);

        _ = await Assert.That(available).IsTrue();
        _ = await Assert.That(client.Refreshes).IsEqualTo(0);
    }

    [Test]
    public async Task Concurrent_callers_near_expiry_trigger_one_refresh_that_is_persisted(CancellationToken cancellationToken)
    {
        ICredentialStore store = new InMemoryCredentialStore();
        await store.Set(
            "openai",
            Credential.ForOAuth(new OAuthCredential
            {
                AccessToken = new Secret("stale"),
                RefreshToken = new Secret("refresh"),
                ExpiresAt = Now.AddMinutes(1),
                AccountId = "acct",
            }),
            cancellationToken);
        var client = new FakeOAuthClient(Now);
        using var sourceOwner = new OAuthTokenSource(store, client, "openai");
        IOAuthTokenSource source = sourceOwner;

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => source.Token(cancellationToken)));

        _ = await Assert.That(client.Refreshes).IsEqualTo(1);
        _ = await Assert.That(results.Select(access => access.AccessToken).Distinct().Single()).IsEqualTo("rotated");
        var oauth = (await store.Get("openai", cancellationToken))?.OAuth;
        _ = await Assert.That(oauth?.RefreshToken.Value).IsEqualTo("rotated-refresh");
        _ = await Assert.That(oauth?.AccountId).IsEqualTo("acct");
    }

    private sealed class FakeOAuthClient(DateTimeOffset now) : IOAuthClient
    {
        public int Refreshes { get; private set; }

        public string AuthorizationUrl(string redirect, string challenge, string state) =>
            throw new NotSupportedException();

        public Task<OAuthCredential> BrowserLogin(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeviceAuthorization> StartDeviceAuthorization(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OAuthCredential> AwaitDeviceAuthorization(DeviceAuthorization device, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
