namespace Parrot.Llm;

/// <summary>Traces one provider request and exposes diagnostics for its attempts.</summary>
internal interface IProviderRequestDiagnostics
{
    /// <summary>Dumps the encoded request body when request dumping is enabled.</summary>
    void DumpRequest(byte[] body);

    /// <summary>Traces provider events and records request lifecycle diagnostics.</summary>
    IAsyncEnumerable<LLMEvent> Trace(
        Func<IProviderAttemptDiagnostics?, CancellationToken, IAsyncEnumerable<LLMEvent>> send,
        string transport,
        CancellationToken cancellationToken);
}
