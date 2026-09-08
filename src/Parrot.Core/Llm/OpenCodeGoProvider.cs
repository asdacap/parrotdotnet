namespace Parrot.Llm;

// Backs the opencode-go provider: streams through the same compatible transport
// as any configured provider and additionally reports subscription usage from
// the /usage endpoint. Composes the base provider rather than inheriting it.
internal sealed class OpenCodeGoProvider : ILLMProvider
{
    private readonly ILLMProvider _inner;

    public OpenCodeGoProvider(OpenAICompatibleOptions options, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inner = new OpenAICompatibleProvider(options, client);
        UsageReporter = new OpenCodeGoUsageReporter(options, client);
    }

    public IUsageReporter? UsageReporter { get; }

    public string Id => _inner.Id;

    public Task<ImageGenerationResult> GenerateImage(ImageGenerationRequest request, CancellationToken cancellationToken) =>
        _inner.GenerateImage(request, cancellationToken);

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
        _inner.HasCredential(cancellationToken);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        _inner.ListModels(cancellationToken);

    public IReadOnlyList<LLMModel> SeedModels() => _inner.SeedModels();

    public ILLMProviderSession OpenSession() => _inner.OpenSession();

    public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
        _inner.Call(request, cancellationToken);
}
