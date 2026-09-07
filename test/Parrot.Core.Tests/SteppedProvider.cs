using Parrot.Llm;

namespace Parrot.Core.Tests;

// Answers one scripted turn per call and holds each answer until the test lets
// it go. The holding is the point: a queue is only observable while something
// is still running, so a provider that answers immediately cannot show one.
//
// ScriptedProvider stays as it is -- four suites depend on its answer-at-once
// behaviour, which is the right double for everything that is not about timing.
internal sealed class SteppedProvider(params LLMEvent[] answers) : ILLMProvider, IDisposable
{
    private readonly Queue<LLMEvent> _answers = new(answers);
    private readonly List<LLMRequest> _requests = [];
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly SemaphoreSlim _released = new(0);

    public string Id => "stepped";

    // Safe to read once Arrived has returned for the call that made it: the
    // provider is parked on _released and writes nothing more.
    public IReadOnlyList<LLMRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public IReadOnlyList<LLMModel> SeedModels() => [];

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LLMModel>>([]);

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LLMEvent answer;

        lock (_gate)
        {
            _requests.Add(request);
            answer = _answers.Count > 0
                ? _answers.Dequeue()
                : LLMEvent.Completed("stop", 1, 0, 1, "nothing scripted", []);
        }

        _ = _arrived.Release();

        // Where an interrupt lands, because this is where a real provider call
        // spends its time.
        await _released.WaitAsync(cancellationToken).ConfigureAwait(false);

        yield return answer;
    }

    // Waits until the session has called the provider.
    public Task Arrived(CancellationToken cancellationToken) => _arrived.WaitAsync(cancellationToken);

    public void Release() => _released.Release();

    public void Dispose()
    {
        _arrived.Dispose();
        _released.Dispose();
    }
}
