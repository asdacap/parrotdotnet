using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskSchedulingProvider : ILLMProvider
{
    private readonly TaskCompletionSource _initialResearchBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _slowRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _initialResearch;
    private int _maximumActive;
    private int _slowExecuting;
    private int _dependentStartedBeforeSlowFinished;

    public string Id => "agent-task-scheduling";

    internal int MaximumActive => _maximumActive;

    internal bool DependentStartedBeforeSlowFinished => _dependentStartedBeforeSlowFinished != 0;

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LLMModel>>([]);

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        SetMaximum(active);
        var prompt = string.Join('\n', request.Messages.Select(message => message.Content));
        var research = prompt.Contains("AgentTask role: research pre-hook", StringComparison.Ordinal);
        var initialResearch = research
            && (prompt.Contains("Task: fast", StringComparison.Ordinal)
                || prompt.Contains("Task: slow", StringComparison.Ordinal));
        try
        {
            if (initialResearch)
            {
                if (Interlocked.Increment(ref _initialResearch) == 2)
                {
                    _ = _initialResearchBarrier.TrySetResult();
                }

                await _initialResearchBarrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (research && prompt.Contains("Task: dependent", StringComparison.Ordinal))
            {
                if (Volatile.Read(ref _slowExecuting) != 0)
                {
                    _ = Interlocked.Exchange(ref _dependentStartedBeforeSlowFinished, 1);
                }

                _ = _slowRelease.TrySetResult();
            }

            if (prompt.Contains("AgentTask role: payload executor", StringComparison.Ordinal)
                && prompt.Contains("Task: slow", StringComparison.Ordinal))
            {
                _ = Interlocked.Exchange(ref _slowExecuting, 1);
                await _slowRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                _ = Interlocked.Exchange(ref _slowExecuting, 0);
            }

            var answer = research
                ? "{\"context\":\"research ready\"}"
                : prompt.Contains("AgentTask role: acceptance reviewer", StringComparison.Ordinal)
                    ? "{\"verdict\":\"accept\",\"evidence\":\"accepted\"}"
                    : "executed";
            yield return LLMEvent.Completed("stop", 1, 0, 1, answer, []);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _active);
        }
    }

    private void SetMaximum(int active)
    {
        while (true)
        {
            var maximum = Volatile.Read(ref _maximumActive);
            if (active <= maximum || Interlocked.CompareExchange(ref _maximumActive, active, maximum) == maximum)
            {
                return;
            }
        }
    }
}
