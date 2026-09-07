using System.Collections.Concurrent;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskQueueProvider(IEnumerable<string> answers) : ILLMProvider
{
    private readonly ConcurrentQueue<string> _answers = new(answers);
    private readonly ConcurrentQueue<LLMRequest> _requests = new();

    public string Id => "agent-task-test";

    internal IReadOnlyList<LLMRequest> Requests => [.. _requests];

    public IReadOnlyList<LLMModel> SeedModels() => [];

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LLMModel>>([]);

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Enqueue(request);
        if (!_answers.TryDequeue(out var answer))
        {
            throw new InvalidOperationException("No AgentTask test response remains.");
        }

        await Task.Yield();
        yield return LLMEvent.Completed("stop", 1, 0, 1, answer, []);
    }
}
