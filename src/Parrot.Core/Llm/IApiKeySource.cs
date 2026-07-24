namespace Parrot.Llm;

internal interface IApiKeySource
{
    ValueTask<string> ApiKey(CancellationToken cancellationToken);
}
