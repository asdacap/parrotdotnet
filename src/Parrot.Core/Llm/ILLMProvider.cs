namespace Parrot.Llm;

internal interface ILLMProvider
{
    string Id { get; }

    Task<LLMResult> Call(LLMRequest request, ILLMEventSink events, CancellationToken cancellationToken);
}
