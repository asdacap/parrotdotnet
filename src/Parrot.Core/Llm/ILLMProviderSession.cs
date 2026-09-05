namespace Parrot.Llm;

internal interface ILLMProviderSession : IAsyncDisposable
{
    void BeginTurn();

    IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken);
}
