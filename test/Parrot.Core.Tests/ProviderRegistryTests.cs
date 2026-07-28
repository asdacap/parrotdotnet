using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ProviderRegistryTests
{
    [Test]
    public async Task Resolve_selects_defaults_and_keeps_the_vendor_prefix()
    {
        var registry = Build(
            [("openrouter", ["openai/gpt-4o", "z"]), ("opencode-go", ["glm-5.2"])],
            new ProviderModel(new FakeProvider("openrouter", true, null), new LLMModel("openai/gpt-4o", "openrouter")));

        var explicitModel = registry.Resolve("openrouter/openai/gpt-4o");
        var defaultModel = registry.Resolve(string.Empty);
        var defaultProvider = registry.Resolve(string.Empty);

        _ = await Assert.That(explicitModel.Model.Id).IsEqualTo("openai/gpt-4o");
        _ = await Assert.That(defaultModel.Model.Id).IsEqualTo("openai/gpt-4o");
        _ = await Assert.That(defaultProvider.Provider.Id).IsEqualTo("openrouter");
    }

    [Test]
    public async Task Resolve_handles_variants_slash_models_exact_precedence_and_ambiguity()
    {
        var provider = new FakeProvider("p", true, null);
        var high = new ModelVariant("high", "xhigh");
        var models = new LLMModel[]
        {
            new("plain", "p")
            {
                Capabilities = new ModelCapabilities(true, true, ["text"], [high]),
            },
            new("vendor/model", "p")
            {
                Capabilities = new ModelCapabilities(true, true, ["text"], [high]),
            },
            new("plain/high", "p"),
            new("ambiguous", "p")
            {
                Capabilities = new ModelCapabilities(true, true, ["text"], [new ModelVariant("path/high", "one")]),
            },
            new("ambiguous/path", "p")
            {
                Capabilities = new ModelCapabilities(true, true, ["text"], [new ModelVariant("high", "two")]),
            },
        };
        var registry = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal) { ["p"] = models },
            null);

        var selected = registry.Resolve("p/vendor/model/high");
        var exact = registry.Resolve("p/plain/high");

        _ = await Assert.That(selected.ModelId).IsEqualTo("vendor/model");
        _ = await Assert.That(selected.Variant?.Name).IsEqualTo("high");
        _ = await Assert.That(selected.Reasoning?.Effort).IsEqualTo("xhigh");
        _ = await Assert.That(selected.Reasoning?.Summary).IsEqualTo("auto");
        _ = await Assert.That(selected.Selector).IsEqualTo("p/vendor/model/high");
        _ = await Assert.That(exact.ModelId).IsEqualTo("plain/high");
        _ = await Assert.That(exact.Variant).IsNull();
        _ = await Assert.That(() => registry.Resolve("p/ambiguous/path/high")).Throws<LLMProviderException>();
    }

    [Test]
    [Arguments("nope/m")]
    [Arguments("p/missing")]
    [Arguments("p")]
    [Arguments("p/")]
    [Arguments("/m")]
    [Arguments("p//m")]
    public async Task Resolve_rejects_unknown_and_malformed_selectors(string selector)
    {
        var registry = Build([("p", ["m"])]);

        _ = await Assert.That(() => registry.Resolve(selector)).Throws<LLMProviderException>();
    }

    [Test]
    public async Task Duplicate_ids_are_rejected_at_construction() =>
        _ = await Assert.That(() => Build([("p", []), ("p", [])])).Throws<LLMProviderException>();

    [Test]
    public async Task Available_models_skips_uncredentialed_providers_and_keeps_seed_on_refresh_failure(
        CancellationToken cancellationToken)
    {
        var unavailable = new FakeProvider("a", false, new LLMModel[] { new("not-listed", "a") });
        var refreshed = new FakeProvider("b", true, new LLMModel[] { new("fresh", "b") });
        var failing = new FakeProvider("c", true, new IOException("offline"));
        var providers = new ILLMProvider[] { unavailable, refreshed, failing };
        var catalogues = new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
        {
            ["a"] = [new LLMModel("seed-a", "a")],
            ["b"] = [new LLMModel("seed-b", "b")],
            ["c"] = [new LLMModel("seed-c", "c")],
        };
        var registry = new ProviderRegistry(providers, catalogues, null);

        var listed = await registry.AvailableModels(cancellationToken);

        _ = await Assert.That(string.Join(",", listed.Select(model => $"{model.Provider.Id}/{model.Model.Id}")))
            .IsEqualTo("b/fresh,c/seed-c");
        _ = await Assert.That(unavailable.ListCalls).IsEqualTo(0);
        _ = await Assert.That(refreshed.ListCalls).IsEqualTo(1);
        _ = await Assert.That(failing.ListCalls).IsEqualTo(1);
        _ = await Assert.That(registry.Models("a").Single().Id).IsEqualTo("seed-a");

        unavailable.HasCredentialValue = true;
        var relisted = await registry.AvailableModels(cancellationToken);

        _ = await Assert.That(string.Join(",", relisted.Select(model => $"{model.Provider.Id}/{model.Model.Id}")))
            .IsEqualTo("a/not-listed,b/fresh,c/seed-c");
        _ = await Assert.That(unavailable.ListCalls).IsEqualTo(1);
    }

    private static ProviderRegistry Build(
        IReadOnlyList<(string Id, string[] Models)> providers,
        ProviderModel? defaultModel = null)
    {
        var builtProviders = providers.Select(entry => (ILLMProvider)new FakeProvider(entry.Id, true, null)).ToList();
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

    private sealed class FakeProvider(string id, bool hasCredential, object? listResult) : ILLMProvider
    {
        public string Id => id;

        public bool HasCredentialValue { get; set; } = hasCredential;

        public int ListCalls { get; private set; }

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) =>
            ValueTask.FromResult(HasCredentialValue);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken)
        {
            ListCalls++;
            return listResult switch
            {
                IReadOnlyList<LLMModel> models => Task.FromResult(models),
                Exception failure => Task.FromException<IReadOnlyList<LLMModel>>(failure),
                _ => throw new NotSupportedException(),
            };
        }

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
