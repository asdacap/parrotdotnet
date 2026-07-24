using Parrot.Llm;

namespace Parrot.Core.Tests;

// Yields a fixed assistant reply and no tool calls. Enough to drive the parts
// of the session and the compactor that do not depend on a real model.
internal sealed class ScriptedProvider(string reply) : ILLMProvider
{
    private readonly List<LLMRequest> _requests = [];

    public string Id => "scripted";

    public IReadOnlyList<LLMRequest> Requests => _requests;

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LLMModel>>([]);

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _requests.Add(request);
        await Task.Yield();
        yield return LLMEvent.TextDelta(reply);
        yield return LLMEvent.Completed("stop", 1, 1, reply, []);
    }
}
