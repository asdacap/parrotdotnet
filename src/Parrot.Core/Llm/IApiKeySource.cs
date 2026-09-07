namespace Parrot.Llm;

// Resolves current API-key credentials; an empty key denotes missing bearer material.
internal interface IApiKeySource
{
    ValueTask<bool> HasCredential(CancellationToken cancellationToken);

    ValueTask<string> ApiKey(CancellationToken cancellationToken);
}
