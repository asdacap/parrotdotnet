using Parrot.Auth;

namespace Parrot.Llm;

internal sealed class StoredApiKeySource(string providerId, string apiKeyEnv, ICredentialStore store) : IApiKeySource
{
    public ValueTask<string> ApiKey(CancellationToken cancellationToken) =>
        apiKeyEnv.Length > 0 && Environment.GetEnvironmentVariable(apiKeyEnv) is { Length: > 0 } fromEnv
            ? ValueTask.FromResult(fromEnv)
            : FromStore(cancellationToken);

    private async ValueTask<string> FromStore(CancellationToken cancellationToken)
    {
        var credential = await store.Get(providerId, cancellationToken).ConfigureAwait(false);
        return credential is { Type: CredentialType.ApiKey, ApiKey: { } apiKey } ? apiKey.Key.Value : string.Empty;
    }
}
