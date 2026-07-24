namespace Parrot.Llm;

internal interface ILLMProvider
{
    string Id { get; }

    Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken);

    Task<LLMResult> Call(LLMRequest request, ILLMEventSink events, CancellationToken cancellationToken);
}
