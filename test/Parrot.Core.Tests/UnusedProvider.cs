using Parrot.Llm;

namespace Parrot.Core.Tests;

// The mapping under test never calls the provider; a stub that throws states
// that, where a mock returning defaults would hide an accidental call.
internal sealed class UnusedProvider : ILLMProvider
{
    public string Id => "unused";

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        throw new NotSupportedException("the mapping does not list models");

    public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the mapping does not call the provider");
}
