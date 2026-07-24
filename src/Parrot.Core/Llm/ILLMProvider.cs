namespace Parrot.Llm;

internal interface ILLMProvider
{
    string Id { get; }

    Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken);

    // Streaming is an IAsyncEnumerable, per MIGRATION.md section 3. The last
    // event is Completed and carries the durable outcome, so there is no second
    // return channel and no sink type that exists only to be passed to one
    // method.
    IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken);
}
