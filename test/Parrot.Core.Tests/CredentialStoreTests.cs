using Parrot.Auth;

namespace Parrot.Core.Tests;

internal sealed class CredentialStoreTests
{
    [Test]
    public async Task Round_trips_both_variants_and_restricts_file_permissions(CancellationToken cancellationToken)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cred-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "credentials.json");

        try
        {
            using var storeOwner = new FileCredentialStore(path);
            ICredentialStore store = storeOwner;
            await store.Set("openrouter", Credential.ForApiKey("sk-123"), cancellationToken);
            await store.Set(
                "chatgpt",
                Credential.ForOAuth(new OAuthCredential
                {
                    AccessToken = new Secret("a"),
                    RefreshToken = new Secret("r"),
                    ExpiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    AccountId = "acct",
                }),
                cancellationToken);

            var apiKey = await store.Get("openrouter", cancellationToken);
            var oauth = await store.Get("chatgpt", cancellationToken);
            var listed = await store.List(cancellationToken);

            _ = await Assert.That(apiKey?.ApiKey?.Key.Value).IsEqualTo("sk-123");
            _ = await Assert.That(oauth?.OAuth?.AccountId).IsEqualTo("acct");
            _ = await Assert.That(listed.Count).IsEqualTo(2);
            _ = await Assert.That(listed[0]).IsEqualTo("chatgpt");
            _ = await Assert.That(listed[1]).IsEqualTo("openrouter");

            if (!OperatingSystem.IsWindows())
            {
                _ = await Assert.That(File.GetUnixFileMode(path))
                    .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    [Arguments("""{"version":1,"credentials":{"x":{"version":1,"type":"oauth"}}}""")]
    [Arguments("""{"version":1,"credentials":{},"unexpected":true}""")]
    [Arguments("""{"version":2,"credentials":{}}""")]
    public async Task A_malformed_or_invalid_store_fails_loudly(string contents, CancellationToken cancellationToken)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cred-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "credentials.json");
        _ = Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, contents, cancellationToken);

        try
        {
            using var storeOwner = new FileCredentialStore(path);
            ICredentialStore store = storeOwner;
            _ = await Assert.That(async () => await store.Get("x", cancellationToken)).Throws<AuthException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
