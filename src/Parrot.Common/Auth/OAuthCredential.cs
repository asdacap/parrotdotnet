using System.Text.Json.Serialization;

namespace Parrot.Auth;

internal sealed record OAuthCredential
{
    [JsonPropertyName("access_token")]
    public Secret AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public Secret RefreshToken { get; init; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("account_id")]
    public string AccountId { get; init; } = string.Empty;

    public static OAuthCredential Create(string accessToken, string refreshToken, DateTimeOffset expiresAt, string accountId) =>
        new()
        {
            AccessToken = new Secret(accessToken),
            RefreshToken = new Secret(refreshToken),
            ExpiresAt = expiresAt,
            AccountId = accountId,
        };
}
