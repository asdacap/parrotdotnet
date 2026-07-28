using System.Net;
using System.Text;
using Parrot.Auth;
using Parrot.Config;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ProviderRegistryTests
{
    [Test]
    public async Task Resolve_selects_defaults_and_keeps_the_vendor_prefix()
    {
        var registry = Build(
            [("openrouter", ["openai/gpt-4o", "z"]), ("opencode-go", ["glm-5.2"])],
            "openrouter/openai/gpt-4o");

        var explicitModel = registry.ResolveCanonical("openrouter/openai/gpt-4o");
        var defaultModel = registry.ResolveCanonical("openrouter/openai/gpt-4o");
        var defaultProvider = registry.ResolveCanonical("openrouter/openai/gpt-4o");

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
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal) { ["p"] = models });

        var selected = registry.ResolveCanonical("p/vendor/model/high");
        var exact = registry.ResolveCanonical("p/plain/high");

        _ = await Assert.That(selected.ModelId).IsEqualTo("vendor/model");
        _ = await Assert.That(selected.Variant?.Name).IsEqualTo("high");
        _ = await Assert.That(selected.Reasoning?.Effort).IsEqualTo("xhigh");
        _ = await Assert.That(selected.Reasoning?.Summary).IsEqualTo("auto");
        _ = await Assert.That(selected.Selector).IsEqualTo("p/vendor/model/high");
        _ = await Assert.That(exact.ModelId).IsEqualTo("plain/high");
        _ = await Assert.That(exact.Variant).IsNull();
        _ = await Assert.That(() => registry.ResolveCanonical("p/ambiguous/path/high")).Throws<LLMProviderException>();
    }

    [Test]
    [Arguments("nope/m")]
    [Arguments("p")]
    [Arguments("p/")]
    [Arguments("/m")]
    [Arguments("p//m")]
    public async Task Resolve_rejects_unknown_providers_and_malformed_selectors(string selector)
    {
        var registry = Build([("p", ["m"])], string.Empty);

        _ = await Assert.That(() => registry.ResolveCanonical(selector)).Throws<LLMProviderException>();
    }

    [Test]
    public async Task Resolve_passes_unlisted_models_to_the_provider_and_preserves_known_variant_syntax()
    {
        var provider = new FakeProvider("p", true, null);
        var registry = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["p"] =
                [
                    new LLMModel("listed", "p")
                    {
                        Capabilities = ModelCapabilities.Create(tools: true, reasoning: true, ["medium"]),
                    },
                ],
            });

        var plain = registry.ResolveCanonical("p/not-listed");
        var variant = registry.ResolveCanonical("p/new-model/medium");

        _ = await Assert.That(plain.ModelId).IsEqualTo("not-listed");
        _ = await Assert.That(plain.Variant).IsNull();
        _ = await Assert.That(variant.ModelId).IsEqualTo("new-model");
        _ = await Assert.That(variant.Variant?.Name).IsEqualTo("medium");
        _ = await Assert.That(variant.Reasoning?.Effort).IsEqualTo("medium");
    }

    [Test]
    public async Task Build_refreshes_catalogues_without_validating_the_configured_model(
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-provider-registry", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.yaml");

        try
        {
            await File.WriteAllTextAsync(
                path,
                "model: configured/not-listed\nproviders:\n  configured:\n    base_url: https://example.test/v1\n",
                cancellationToken);
            var store = new InMemoryCredentialStore();
            await store.Set("configured", Credential.ForApiKey("key"), cancellationToken);
            using var handler = new ModelsHandler();
            using var client = new HttpClient(handler, disposeHandler: false);
            var registry = await new ProviderRegistryBuilder(
                Configuration.Load(path, Path.Combine(directory, "predefined_config.yaml")),
                store,
                client,
                new SystemBrowserOpener(static _ => null)).Build(cancellationToken);

            var selected = registry.ResolveCanonical("configured/not-listed");

            _ = await Assert.That(registry.Models("configured").Single().Id).IsEqualTo("live-model");
            _ = await Assert.That(selected.ModelId).IsEqualTo("not-listed");
            _ = await Assert.That(handler.Requests).IsGreaterThanOrEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Duplicate_ids_are_rejected_at_construction() =>
        _ = await Assert.That(() => Build([("p", []), ("p", [])], string.Empty)).Throws<LLMProviderException>();

    [Test]
    public async Task Resolve_preserves_requested_alias_identity_and_canonical_slash_model_route()
    {
        var registry = Build([("p", ["vendor/model"])], string.Empty);
        var catalog = new ModelAliasCatalog(
            registry,
            [new("preferred", "p/vendor/model", "primary", "system prompt")]);
        var router = new ModelRouter(registry, catalog, string.Empty);

        var selection = router.Resolve("preferred");

        _ = await Assert.That(selection.RequestedSelector.Value).IsEqualTo("preferred");
        _ = await Assert.That(selection.Alias?.Name).IsEqualTo("preferred");
        _ = await Assert.That(selection.CanonicalModel.Selector).IsEqualTo("p/vendor/model");
        _ = await Assert.That(selection.CanonicalBase).IsEqualTo("p/vendor/model");
    }

    [Test]
    public async Task Resolve_uses_default_alias_and_accepts_unlisted_alias_target()
    {
        var registry = Build([("p", ["listed"])], string.Empty);
        var catalog = new ModelAliasCatalog(
            registry,
            [
                new("default", "p/not-listed", "primary", null),
                new("unlisted", "p/also-not-listed", "secondary", null),
            ]);
        var router = new ModelRouter(registry, catalog, "default");

        var defaultSelection = router.Resolve(string.Empty);
        var unlistedSelection = router.Resolve("unlisted");

        _ = await Assert.That(defaultSelection.Alias?.Name).IsEqualTo("default");
        _ = await Assert.That(defaultSelection.CanonicalModel.Selector).IsEqualTo("p/not-listed");
        _ = await Assert.That(unlistedSelection.CanonicalModel.Selector).IsEqualTo("p/also-not-listed");
    }

    [Test]
    public async Task Resolve_rejects_disabled_alias()
    {
        var registry = Build([("p", ["listed"])], string.Empty);
        var catalog = new ModelAliasCatalog(registry, [new("disabled", string.Empty, "primary", null)]);
        var router = new ModelRouter(registry, catalog, string.Empty);

        _ = await Assert.That(() => router.Resolve("disabled")).Throws<LLMProviderException>();
    }

    [Test]
    public async Task Replace_retargets_future_resolutions_while_captured_snapshot_keeps_old_alias()
    {
        var registry = Build([("p", ["old", "new"])], string.Empty);
        var catalog = new ModelAliasCatalog(registry, [new("preferred", "p/old", "primary", null)]);
        var router = new ModelRouter(registry, catalog, string.Empty);
        var beforeReplacement = router.Resolve("preferred");

        catalog.Replace([new("preferred", "p/new", "primary", null)]);
        var afterReplacement = router.Resolve("preferred");

        _ = await Assert.That(beforeReplacement.CanonicalModel.Selector).IsEqualTo("p/old");
        _ = await Assert.That(beforeReplacement.AliasSnapshot.Find("preferred")?.ModelString).IsEqualTo("p/old");
        _ = await Assert.That(afterReplacement.CanonicalModel.Selector).IsEqualTo("p/new");
        _ = await Assert.That(afterReplacement.AliasSnapshot.Find("preferred")?.ModelString).IsEqualTo("p/new");
    }

    [Test]
    public async Task Replacement_rejections_are_atomic_and_disallow_self_or_chained_aliases()
    {
        var registry = Build([("p", ["old"])], string.Empty);
        var catalog = new ModelAliasCatalog(registry, [new("preferred", "p/old", "primary", null)]);

        _ = await Assert.That(() => new ModelAliasCatalog(
            registry,
            [new("self", "self", "primary", null)])).Throws<LLMProviderException>();
        _ = await Assert.That(() => new ModelAliasCatalog(
            registry,
            [new("first", "second", "primary", null), new("second", "p/old", "primary", null)]))
            .Throws<LLMProviderException>();
        _ = await Assert.That(() => catalog.Replace([new("preferred", "preferred", "primary", null)]))
            .Throws<LLMProviderException>();
        _ = await Assert.That(catalog.Capture().Find("preferred")?.ModelString).IsEqualTo("p/old");
    }

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
        var registry = new ProviderRegistry(providers, catalogues);

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
        string defaultSelector)
    {
        var builtProviders = providers.Select(entry => (ILLMProvider)new FakeProvider(entry.Id, true, null)).ToList();
        var catalogues = new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal);

        foreach (var (id, models) in providers)
        {
            catalogues[id] = [.. models.Select(model => new LLMModel(model, id))];
        }

        _ = defaultSelector;
        return new ProviderRegistry(builtProviders, catalogues);
    }

    private sealed class ModelsHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"live-model","context_window":321}]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
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
