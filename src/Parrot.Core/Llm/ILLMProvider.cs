namespace Parrot.Llm;

public interface ILLMProvider
{
    string Id { get; }

    Task<LLMResult> Call(LLMRequest request, ILLMEventSink events, CancellationToken cancellationToken);
}
