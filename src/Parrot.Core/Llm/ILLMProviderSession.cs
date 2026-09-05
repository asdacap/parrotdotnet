namespace Parrot.Llm;

internal interface ILLMProviderSession : IAsyncDisposable
{
    IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken);
}
