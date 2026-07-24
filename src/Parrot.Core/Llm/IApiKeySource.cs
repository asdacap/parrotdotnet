namespace Parrot.Llm;

internal interface IApiKeySource
{
    ValueTask<bool> HasCredential(CancellationToken cancellationToken);

    ValueTask<string> ApiKey(CancellationToken cancellationToken);
}
