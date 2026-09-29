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

    private static Credential Parse(string description)
    {
        var parts = description.Split(':');
        return parts[0] == "key"
            ? Credential.ForApiKey(parts[1])
            : Credential.ForOAuth(OAuthCredential.Create(parts[2], parts[2], DateTimeOffset.UnixEpoch, parts[1]));
    }
}
