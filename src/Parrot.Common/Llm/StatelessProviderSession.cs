namespace Parrot.Llm;

internal sealed class StatelessProviderSession(
    Func<LLMRequest, CancellationToken, IAsyncEnumerable<LLMEvent>> call) : ILLMProviderSession
{
    public void BeginTurn()
    {
    }

    public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
        call(request, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
