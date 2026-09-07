using System.Buffers.Text;
using System.Text;
using Parrot.Auth;

namespace Parrot.Core.Tests;

internal sealed class OpenAiOAuthClientTests
{
    [Test]
    public async Task Authorization_url_carries_the_exact_pkce_parameters(CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        IOAuthClient client = new OpenAiOAuthClient(http, new NoopBrowserOpener(), new OpenAiOAuthOptions());

        var url = client.AuthorizationUrl("http://localhost:1455/auth/callback", "CHALLENGE", "STATE");

        _ = await Assert.That(url).StartsWith("https://auth.openai.com/oauth/authorize?");
        _ = await Assert.That(url).Contains("response_type=code");
        _ = await Assert.That(url).Contains($"client_id={OpenAiOAuthClient.ClientId}");
        _ = await Assert.That(url).Contains("scope=openid%20profile%20email%20offline_access");
        _ = await Assert.That(url).Contains("code_challenge=CHALLENGE");
        _ = await Assert.That(url).Contains("code_challenge_method=S256");
        _ = await Assert.That(url).Contains("id_token_add_organizations=true");
        _ = await Assert.That(url).Contains("codex_cli_simplified_flow=true");
        _ = await Assert.That(url).Contains("state=STATE");
        _ = await Assert.That(url).Contains("originator=opencode");
        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }

    [Test]
    [Arguments("""{"chatgpt_account_id":"top"}""", "top")]
    [Arguments("""{"https://api.openai.com/auth":{"chatgpt_account_id":"nested"}}""", "nested")]
    [Arguments("""{"organizations":[{"id":"org-1"}]}""", "org-1")]
    [Arguments("""{"unrelated":true}""", "")]
    public async Task Account_id_is_read_from_unverified_jwt_claims(string payloadJson, string expected)
    {
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));
        var token = $"header.{payload}.signature";

        _ = await Assert.That(OpenAiOAuthClient.ExtractAccountId(token)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_token_without_three_segments_yields_no_account() =>
        _ = await Assert.That(OpenAiOAuthClient.ExtractAccountId("not-a-jwt")).IsEmpty();

    private sealed class NoopBrowserOpener : IBrowserOpener
    {
        public Task Open(string url, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
