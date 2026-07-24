using System.Text.Json.Serialization;

namespace Parrot.Auth;

// A versioned tagged union: exactly one variant is present. Behaviour lives on
// the type that owns the state, so validation is a method here rather than a
// free function. Port of Go's auth.Credential.
internal sealed record Credential
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("type")]
    public CredentialType Type { get; init; }

    [JsonPropertyName("api_key")]
    public ApiKeyCredential? ApiKey { get; init; }

    [JsonPropertyName("oauth")]
    public OAuthCredential? OAuth { get; init; }

    public static Credential ForApiKey(string key) =>
        new() { Version = CurrentVersion, Type = CredentialType.ApiKey, ApiKey = new ApiKeyCredential { Key = new Secret(key) } };

    public static Credential ForOAuth(OAuthCredential value) =>
        new() { Version = CurrentVersion, Type = CredentialType.OAuth, OAuth = value };

    public void Validate()
    {
        if (Version != CurrentVersion)
        {
            throw new AuthException($"auth: unsupported credential version {Version}");
        }

        switch (Type)
        {
            case CredentialType.ApiKey when ApiKey is null || OAuth is not null || ApiKey.Key.Value.Length == 0:
                throw new AuthException("auth: invalid api key credential");

            case CredentialType.OAuth when OAuth is null || ApiKey is not null
                || OAuth.AccessToken.Value.Length == 0 || OAuth.RefreshToken.Value.Length == 0
                || OAuth.ExpiresAt == default:
                throw new AuthException("auth: invalid oauth credential");

            case CredentialType.ApiKey:
            case CredentialType.OAuth:
                break;

            default:
                throw new AuthException("auth: unknown credential type");
        }
    }
}
