using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskCompositeInterleavingProvider : ILLMProvider, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<LLMRequest> _requests = [];
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly SemaphoreSlim _nestedRelease = new(0);
    private readonly SemaphoreSlim _unrelatedRelease = new(0);

    public string Id => "agent-task-composite-interleaving";

    internal IReadOnlyList<LLMRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LLMModel>>([]);

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = request.Messages.Last(message => message.Role == LLMRole.User).Content;
        lock (_gate)
        {
            _requests.Add(request);
        }

        _ = _arrived.Release();
        string answer;
        if (prompt.Contains("AgentTask role: prepare", StringComparison.Ordinal))
        {
            answer = "{\"context\":\"parent preparation\"}";
        }
        else if (prompt.Contains("Task: nested", StringComparison.Ordinal))
        {
            await _nestedRelease.WaitAsync(cancellationToken).ConfigureAwait(false);
            answer = "{\"result\":\"nested result\",\"verdict\":\"accept\",\"evidence\":\"nested proof\"}";
        }
        else if (prompt.Contains("unrelated message", StringComparison.Ordinal))
        {
            await _unrelatedRelease.WaitAsync(cancellationToken).ConfigureAwait(false);
            answer = "unrelated answer";
        }
        else if (prompt.Contains("AgentTask role: acceptance reviewer", StringComparison.Ordinal))
        {
            answer = "{\"verdict\":\"accept\",\"evidence\":\"parent proof\"}";
        }
        else
        {
            throw new InvalidOperationException($"Unexpected AgentTask test prompt: {prompt}");
        }

        yield return LLMEvent.Completed("stop", 1, 0, 1, answer, []);
    }

    public Task WaitForRequest(CancellationToken cancellationToken) =>
        _arrived.WaitAsync(cancellationToken);

    public void FinishNested() => _nestedRelease.Release();

    public void FinishUnrelated() => _unrelatedRelease.Release();

    public void Dispose()
    {
        _arrived.Dispose();
        _nestedRelease.Dispose();
        _unrelatedRelease.Dispose();
    }
}
