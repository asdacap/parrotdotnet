namespace Parrot.Llm;

internal sealed class UsageReportingProvider(ILLMProvider inner, IUsageReporter reporter) : ILLMProvider
{
    public IUsageReporter? UsageReporter => reporter;

    public string Id => inner.Id;

    public long CalculateImageTokens(LLMModel model, LLMContent image) => inner.CalculateImageTokens(model, image);

    public Task<ImageGenerationResult> GenerateImage(ImageGenerationRequest request, CancellationToken cancellationToken) =>
        inner.GenerateImage(request, cancellationToken);

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
        inner.HasCredential(cancellationToken);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        inner.ListModels(cancellationToken);

    public IReadOnlyList<LLMModel> SeedModels() => inner.SeedModels();

    public ILLMProviderSession OpenSession() => inner.OpenSession();

    public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
        inner.Call(request, cancellationToken);
}
