using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class TerminalFailureProvider(string message) : ILLMProvider
{
    public string Id => "terminal-failure";

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LLMModel>>([]);

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = request;
        await Task.Yield();
        yield return LLMEvent.TextDelta("partial");
        throw new InvalidOperationException(message);
    }
}
