namespace Parrot.Llm;

// Supplies model discovery and calls; sessions and optional usage reporters borrow provider resources.
internal interface ILLMProvider
{
    string Id { get; }

    // Null means the provider does not support account usage reporting.
    IUsageReporter? UsageReporter => null;

    // Estimates one image locally for this model and current wire preprocessing, without an API request.
    long CalculateImageTokens(LLMModel model, LLMContent image) => FallbackImageTokenCalculator.ImageTokens;

    // Generates one PNG or edits supplied image bytes; unsupported providers throw without a request.
    Task<ImageGenerationResult> GenerateImage(ImageGenerationRequest request, CancellationToken cancellationToken) =>
        throw new LLMProviderException("provider: image generation is not supported");

    // Returns locally available model metadata without fetching the remote catalogue.
    IReadOnlyList<LLMModel> SeedModels();

    ValueTask<bool> HasCredential(CancellationToken cancellationToken);

    Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken);

    // The caller owns the session and must dispose it after all calls finish.
    ILLMProviderSession OpenSession() => new StatelessProviderSession(this);

    // Streaming is an IAsyncEnumerable, per MIGRATION.md section 3. The last
    // event is Completed and carries the durable outcome, so there is no second
    // return channel and no sink type that exists only to be passed to one
    // method.
    IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken);
}
