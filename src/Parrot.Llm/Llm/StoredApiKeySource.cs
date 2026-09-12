using Parrot.Auth;

namespace Parrot.Llm;

internal sealed class StoredApiKeySource(string providerId, string apiKeyEnv, ICredentialStore store) : IApiKeySource
{
    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
        apiKeyEnv.Length > 0 && Environment.GetEnvironmentVariable(apiKeyEnv) is { Length: > 0 }
            ? ValueTask.FromResult(true)
            : HasStoredCredential(cancellationToken);

    public ValueTask<string> ApiKey(CancellationToken cancellationToken) =>
        apiKeyEnv.Length > 0 && Environment.GetEnvironmentVariable(apiKeyEnv) is { Length: > 0 } fromEnv
            ? ValueTask.FromResult(fromEnv)
            : FromStore(cancellationToken);

    private async ValueTask<bool> HasStoredCredential(CancellationToken cancellationToken)
    {
        var credential = await store.Get(providerId, cancellationToken).ConfigureAwait(false);
        return credential is
        {
            Version: Credential.CurrentVersion,
            Type: CredentialType.ApiKey,
            ApiKey.Key.Value.Length: > 0,
            OAuth: null,
        };
    }

    private async ValueTask<string> FromStore(CancellationToken cancellationToken)
    {
        var credential = await store.Get(providerId, cancellationToken).ConfigureAwait(false);
        return credential is { Type: CredentialType.ApiKey, ApiKey: { } apiKey } ? apiKey.Key.Value : string.Empty;
    }
}
