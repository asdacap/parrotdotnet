using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskSchedulingProvider : ILLMProvider
{
    private readonly TaskCompletionSource _initialPayloadBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _slowRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _initialPayload;
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
        var prompt = request.Messages.Last(message => message.Role == LLMRole.User).Content;
        var acceptance = prompt.Contains("AgentTask role: acceptance reviewer", StringComparison.Ordinal);
        var preparation = !acceptance && prompt.Contains("AgentTask role: prepare", StringComparison.Ordinal);
        var combinedPayload = !acceptance
            && prompt.Contains("AgentTask role: payload executor", StringComparison.Ordinal)
            && prompt.Contains("Inspect, implement, and verify this instruction:", StringComparison.Ordinal);
        var initialPayload = combinedPayload
            && (prompt.Contains("Task: fast", StringComparison.Ordinal)
                || prompt.Contains("Task: slow", StringComparison.Ordinal));
        try
        {
            if (initialPayload)
            {
                if (Interlocked.Increment(ref _initialPayload) == 2)
                {
                    _ = _initialPayloadBarrier.TrySetResult();
                }

                await _initialPayloadBarrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (combinedPayload && prompt.Contains("Task: dependent", StringComparison.Ordinal))
            {
                if (Volatile.Read(ref _slowExecuting) != 0)
                {
                    _ = Interlocked.Exchange(ref _dependentStartedBeforeSlowFinished, 1);
                }

                _ = _slowRelease.TrySetResult();
            }

            if (combinedPayload && prompt.Contains("Task: slow", StringComparison.Ordinal))
            {
                _ = Interlocked.Exchange(ref _slowExecuting, 1);
                await _slowRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                _ = Interlocked.Exchange(ref _slowExecuting, 0);
            }

            var answer = preparation
                ? "{\"context\":\"preparation ready\"}"
                : acceptance
                    ? "{\"verdict\":\"accept\",\"evidence\":\"accepted\"}"
                    : combinedPayload
                        ? "{\"result\":\"payload ready\",\"verdict\":\"accept\",\"evidence\":\"accepted\"}"
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
