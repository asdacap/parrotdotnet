using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ProviderRegistryTests
{
    [Test]
    public async Task Resolve_selects_defaults_and_keeps_the_vendor_prefix()
    {
        var registry = Build(
            [("openrouter", ["openai/gpt-4o", "z"]), ("opencode-go", ["glm-5.2"])],
            new ProviderModel(new FakeProvider("openrouter"), new LLMModel("openai/gpt-4o", "openrouter")));

        var explicitModel = registry.Resolve("openrouter", "openai/gpt-4o");
        var defaultModel = registry.Resolve("openrouter", string.Empty);
        var defaultProvider = registry.Resolve(string.Empty, string.Empty);

        _ = await Assert.That(explicitModel.Model.Id).IsEqualTo("openai/gpt-4o");
        _ = await Assert.That(defaultModel.Model.Id).IsEqualTo("openai/gpt-4o");
        _ = await Assert.That(defaultProvider.Provider.Id).IsEqualTo("openrouter");
    }

    [Test]
    public async Task Resolve_rejects_unknown_providers_and_models()
    {
        var registry = Build([("p", ["m"])]);

        _ = await Assert.That(() => registry.Resolve("nope", "m")).Throws<LLMProviderException>();
        _ = await Assert.That(() => registry.Resolve("p", "missing")).Throws<LLMProviderException>();
    }

    [Test]
    public async Task Duplicate_ids_are_rejected_at_construction() =>
        _ = await Assert.That(() => Build([("p", []), ("p", [])])).Throws<LLMProviderException>();

    private static ProviderRegistry Build(
        IReadOnlyList<(string Id, string[] Models)> providers,
        ProviderModel? defaultModel = null)
    {
        var builtProviders = providers.Select(entry => (ILLMProvider)new FakeProvider(entry.Id)).ToList();
        var catalogues = new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal);

        foreach (var (id, models) in providers)
        {
            catalogues[id] = [.. models.Select(model => new LLMModel(model, id))];
        }

        var resolvedDefault = defaultModel is null
            ? null
            : new ProviderModel(
                builtProviders.First(provider => provider.Id == defaultModel.Provider.Id),
                new LLMModel(defaultModel.Model.Id, defaultModel.Provider.Id));
        return new ProviderRegistry(builtProviders, catalogues, resolvedDefault);
    }

    private sealed class FakeProvider(string id) : ILLMProvider
    {
        public string Id => id;

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
