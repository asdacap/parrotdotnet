using Parrot.Auth;

namespace Parrot.Core.Tests;

internal sealed class CredentialStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cred-").FullName;

    private string StorePath => Path.Combine(_directory, "credentials.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task Round_trips_all_variants_and_restricts_file_permissions(CancellationToken cancellationToken)
    {
        using var storeOwner = new FileCredentialStore(StorePath);
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

        await store.Set(S3Credential.ImageUploadName, Credential.ForS3("access", "secret"), cancellationToken);

        var apiKey = await store.Get("openrouter", cancellationToken);
        var oauth = await store.Get("chatgpt", cancellationToken);
        var s3 = await store.Get(S3Credential.ImageUploadName, cancellationToken);
        var listed = await store.List(cancellationToken);

        _ = await Assert.That(apiKey?.ApiKey?.Key.Value).IsEqualTo("sk-123");
        _ = await Assert.That(oauth?.OAuth?.AccountId).IsEqualTo("acct");
        _ = await Assert.That(listed.Count).IsEqualTo(3);
        _ = await Assert.That(listed[0]).IsEqualTo("chatgpt");
        _ = await Assert.That(listed[1]).IsEqualTo("image_upload");
        _ = await Assert.That(listed[2]).IsEqualTo("openrouter");
        _ = await Assert.That(s3?.S3?.AccessKey.Value).IsEqualTo("access");
        _ = await Assert.That(s3?.S3?.SecretKey.Value).IsEqualTo("secret");
        _ = await Assert.That(s3?.ToString()).DoesNotContain("access").And.DoesNotContain("secret");

        if (!OperatingSystem.IsWindows())
        {
            _ = await Assert.That(File.GetUnixFileMode(StorePath))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var externalWriter = new FileCredentialStore(StorePath);
        await externalWriter.Set("external", Credential.ForApiKey("sk-789"), cancellationToken);
        _ = await Assert.That((await store.Get("external", cancellationToken))?.ApiKey?.Key.Value).IsEqualTo("sk-789");
    }

    [Test]
    [Arguments("""{"version":1,"credentials":{"x":{"version":1,"type":"oauth"}}}""")]
    [Arguments("""{"version":1,"credentials":{},"unexpected":true}""")]
    [Arguments("""{"version":2,"credentials":{}}""")]
    public async Task A_malformed_or_invalid_store_fails_loudly(string contents, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(StorePath, contents, cancellationToken);

        using var storeOwner = new FileCredentialStore(StorePath);
        ICredentialStore store = storeOwner;
        _ = await Assert.That(async () => await store.Get("x", cancellationToken)).Throws<AuthException>();
    }

    [Test]
    public async Task An_unrecognised_entry_is_isolated_and_preserved_on_write(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            StorePath,
            """{"version":1,"credentials":{"future":{"version":1,"type":"future","future":{"x":1}},"openrouter":{"version":1,"type":"api_key","api_key":{"key":"sk-123"}}}}""",
            cancellationToken);

        using var storeOwner = new FileCredentialStore(StorePath);
        ICredentialStore store = storeOwner;
        await store.Set("other", Credential.ForApiKey("sk-456"), cancellationToken);

        var apiKey = await store.Get("openrouter", cancellationToken);
        var listed = await store.List(cancellationToken);

        _ = await Assert.That(apiKey?.ApiKey?.Key.Value).IsEqualTo("sk-123");
        _ = await Assert.That(listed).IsEquivalentTo(["future", "openrouter", "other"]);
        _ = await Assert.That(async () => await store.Get("future", cancellationToken)).Throws<AuthException>();
        _ = await Assert.That(await File.ReadAllTextAsync(StorePath, cancellationToken)).Contains("\"type\": \"future\"");
    }
}
