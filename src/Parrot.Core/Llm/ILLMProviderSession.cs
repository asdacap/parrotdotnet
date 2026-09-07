namespace Parrot.Llm;

// Owns per-session transport state across calls; disposal releases its transport, not the provider.
internal interface ILLMProviderSession : IAsyncDisposable
{
    // Starts a user turn, clearing turn-specific metadata without resetting transport selection.
    void BeginTurn();

    // Streams a call within this session; finish or dispose the enumerator before changing transport.
    IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken);

    // Observes otherwise silent retries for this call only; observer failures must not affect the call.
    IAsyncEnumerable<LLMEvent> CallWithRetryObservation(
        LLMRequest request,
        Action<int, TimeSpan> observeRetry,
        CancellationToken cancellationToken) => Call(request, cancellationToken);

    // Selects HTTP for subsequent calls, waiting for the current call without caller cancellation.
    // Returns false when unsupported; a successful selection persists across BeginTurn calls.
    ValueTask<bool> TryFallBackToHttp() => ValueTask.FromResult(false);
}
