using Parrot.Auth;

namespace Parrot.Core.Tests;

internal sealed class CredentialTests
{
    [Test]
    [Arguments("key:a", "key:a", true)]
    [Arguments("key:a", "key:b", false)]
    [Arguments("oauth:account-a:token-1", "oauth:account-a:token-2", true)]
    [Arguments("oauth:account-a:token-1", "oauth:account-b:token-1", false)]
    [Arguments("key:account-a", "oauth:account-a:token-1", false)]
    public async Task Matches_compares_the_account_and_ignores_rotating_tokens(string left, string right, bool expected) =>
        _ = await Assert.That(Parse(left).Matches(Parse(right))).IsEqualTo(expected);

    [Test]
    [Arguments("", "secret")]
    [Arguments("access", "")]
    [Arguments(" ", "secret")]
    public async Task Empty_s3_keys_are_rejected(string accessKey, string secretKey) =>
        _ = await Assert.That(() => Credential.ForS3(accessKey, secretKey).Validate()).Throws<AuthException>();

    [Test]
    public async Task S3_matches_both_keys_and_rejects_mixed_variants()
    {
        var credential = Credential.ForS3("access", "secret");
        _ = await Assert.That(credential.Matches(Credential.ForS3("access", "secret"))).IsTrue();
        _ = await Assert.That(credential.Matches(Credential.ForS3("other", "secret"))).IsFalse();
        _ = await Assert.That(credential.Matches(Credential.ForS3("access", "other"))).IsFalse();
        _ = await Assert.That(() => (credential with { ApiKey = new ApiKeyCredential { Key = new Secret("key") } }).Validate())
            .Throws<AuthException>();
    }

    private static Credential Parse(string description)
    {
        var parts = description.Split(':');
        return parts[0] == "key"
            ? Credential.ForApiKey(parts[1])
            : Credential.ForOAuth(OAuthCredential.Create(parts[2], parts[2], DateTimeOffset.UnixEpoch, parts[1]));
    }
}
