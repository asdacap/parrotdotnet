using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskBlockingProvider : ILLMProvider, IDisposable
{
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id => "agent-task-blocking";

    public IReadOnlyList<LLMModel> SeedModels() => [];

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LLMModel>>([]);

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = request;
        _ = _arrived.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    public void Dispose() => _ = _arrived.TrySetCanceled();

    internal Task WaitUntilArrived(CancellationToken cancellationToken) => _arrived.Task.WaitAsync(cancellationToken);
}
