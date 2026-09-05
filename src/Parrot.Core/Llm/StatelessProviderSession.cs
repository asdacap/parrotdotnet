namespace Parrot.Llm;

internal sealed class StatelessProviderSession(ILLMProvider provider) : ILLMProviderSession
{
    public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
        provider.Call(request, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
