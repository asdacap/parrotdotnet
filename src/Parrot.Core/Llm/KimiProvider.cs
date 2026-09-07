namespace Parrot.Llm;

// Backs the kimi-api provider: the Moonshot platform API, billed against a
// prepaid balance. Streams over the same compatible transport and additionally
// reports that balance. Composes the base provider rather than inheriting it.
internal sealed class KimiProvider : ILLMProvider
{
    private readonly ILLMProvider _inner;

    public KimiProvider(OpenAICompatibleOptions options, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inner = new OpenAICompatibleProvider(options, client);
        UsageReporter = new KimiUsageReporter(options, client);
    }

    public IUsageReporter? UsageReporter { get; }

    public string Id => _inner.Id;

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
        _inner.HasCredential(cancellationToken);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        _inner.ListModels(cancellationToken);

    public IReadOnlyList<LLMModel> SeedModels() => _inner.SeedModels();

    public ILLMProviderSession OpenSession() => _inner.OpenSession();

    public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
        _inner.Call(request, cancellationToken);
}
