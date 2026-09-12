using System.Net;
using System.Text;
using System.Text.Json;
using Parrot.Auth;
using Parrot.Config;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ProviderRegistryTests
{
    [Test]
    [Arguments(1)]
    [Arguments(67108864)]
    public async Task Openrouter_predefined_preferences_reach_the_request_body(int maximumRequestBytes, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-openrouter-preferences", Guid.NewGuid().ToString("n"));
        var store = new InMemoryCredentialStore();
        await store.Set("openrouter", Credential.ForApiKey("key"), cancellationToken);
        using var handler = new OpenRouterHandler();
        using var client = new HttpClient(handler, disposeHandler: false);

        try
        {
            var configurationPath = Path.Combine(directory, "config.yaml");
            _ = Directory.CreateDirectory(directory);
            var userConfiguration = $$"""
                request_limits:
                  provider_request_bytes: {{maximumRequestBytes}}
                providers:
                  kimi-api:
                    api_key_env: ''
                  kimi-code:
                    api_key_env: ''
                  opencode-go:
                    api_key_env: ''
                """;
            await File.WriteAllTextAsync(configurationPath, userConfiguration, cancellationToken);
            var configuration = Configuration.Load(
                configurationPath,
                Path.Combine(directory, "predefined_config.yaml"));
            using var httpClients = new ProviderHttpClientCatalog(client);
            var registry = await new ProviderRegistryBuilder(
                configuration,
                store,
                httpClients,
                new SystemBrowserOpener(static _ => null),
                new ModelsDevInformationProvider(client)).Build(cancellationToken);
            var provider = registry.List().Single(item => item.Id == "openrouter");
            var request = new LLMRequest
            {
                Model = "vendor/model",
                Messages = [LLMMessage.User("hello")],
            };

            if (maximumRequestBytes == 1)
            {
                async Task Consume()
                {
                    await foreach (var item in provider.Call(request, cancellationToken))
                    {
                        _ = item;
                    }
                }

                _ = await Assert.That(Consume).Throws<ProviderHttpException>().WithMessageContaining("exceeds 1 bytes");
                _ = await Assert.That(handler.RequestBody).IsEqualTo(string.Empty);
                return;
            }

            await foreach (var item in provider.Call(request, cancellationToken))
            {
                _ = item;
            }

            using var document = JsonDocument.Parse(handler.RequestBody);
            var root = document.RootElement;
            var preferences = root.GetProperty("provider");
            _ = await Assert.That(preferences.EnumerateObject().Count()).IsEqualTo(4);
            _ = await Assert.That(preferences.GetProperty("allow_fallbacks").GetBoolean()).IsTrue();
            _ = await Assert.That(preferences.GetProperty("require_parameters").GetBoolean()).IsTrue();
            _ = await Assert.That(preferences.GetProperty("data_collection").GetString()).IsEqualTo("deny");
            _ = await Assert.That(preferences.GetProperty("zdr").GetBoolean()).IsTrue();
            _ = await Assert.That(root.GetProperty("include_router_metadata").GetBoolean()).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_selects_defaults_and_keeps_the_vendor_prefix()
    {
        var registry = new ProviderRegistry(
            [new FakeProvider("openrouter", true, null), new FakeProvider("opencode-go", true, null)],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["openrouter"] = [new LLMModel("openai/gpt-4o", "openrouter"), new LLMModel("z", "openrouter")],
                ["opencode-go"] = [new LLMModel("glm-5.2", "opencode-go")],
            });

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
        var registry = new ProviderRegistry(
            [new FakeProvider("p", true, null)],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["p"] = [new LLMModel("m", "p")],
            });

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
    public async Task Build_activates_predefined_openai_with_a_seeded_model_catalogue(
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-openai-provider", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.yaml");

        try
        {
            await File.WriteAllTextAsync(path, string.Empty, cancellationToken);
            var store = new InMemoryCredentialStore();
            await store.Set("openai", Credential.ForApiKey("placeholder"), cancellationToken);
            using var handler = new OpenAiModelsHandler();
            using var client = new HttpClient(handler, disposeHandler: false);
            using var httpClients = new ProviderHttpClientCatalog(client);
            var registry = await new ProviderRegistryBuilder(
                Configuration.Load(path, Path.Combine(directory, "predefined_config.yaml")),
                store,
                httpClients,
                new SystemBrowserOpener(static _ => null),
                new ModelsDevInformationProvider(client)).Build(cancellationToken);

            _ = await Assert.That(registry.List().Select(provider => provider.Id)).Contains("openai");
            _ = await Assert.That(registry.Models("openai").Select(model => model.Id)).Contains("gpt-5.4");
            _ = await Assert.That(handler.Requests).IsGreaterThanOrEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
            using var httpClients = new ProviderHttpClientCatalog(client);
            var registry = await new ProviderRegistryBuilder(
                Configuration.Load(path, Path.Combine(directory, "predefined_config.yaml")),
                store,
                httpClients,
                new SystemBrowserOpener(static _ => null),
                new ModelsDevInformationProvider(client)).Build(cancellationToken);

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
    public async Task Build_seeds_custom_provider_from_configured_model_defaults(
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-provider-defaults", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.yaml");

        try
        {
            await File.WriteAllTextAsync(
                path,
                "providers:\n  custom:\n    base_url: https://example.test/v1\n    model_defaults:\n      seed:\n        name: Seed Name\n        context: 123\n        variants:\n          high:\n            reasoning_effort: high\n          low:\n            reasoning_effort: low\n",
                cancellationToken);
            using var handler = new ModelsHandler();
            using var client = new HttpClient(handler, disposeHandler: false);
            using var httpClients = new ProviderHttpClientCatalog(client);
            var registry = await new ProviderRegistryBuilder(
                Configuration.Load(path, Path.Combine(directory, "predefined_config.yaml")),
                new InMemoryCredentialStore(),
                httpClients,
                new SystemBrowserOpener(static _ => null),
                new ModelsDevInformationProvider(client)).Build(cancellationToken);

            var seed = registry.Models("custom").Single();

            _ = await Assert.That(seed.Id).IsEqualTo("seed");
            _ = await Assert.That(seed.Name).IsEqualTo("Seed Name");
            _ = await Assert.That(seed.ContextWindow).IsEqualTo(123);
            _ = await Assert.That(string.Join(",", seed.Capabilities.Variants.Select(item => item.Name)))
                .IsEqualTo("high,low");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Chatgpt_refresh_keeps_declarations_drops_unserved_defaults_and_uses_served_default_metadata(
        CancellationToken cancellationToken)
    {
        using var handler = new ChatGptModelsHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new ChatGptProvider(
            new FakeOAuthTokenSource(),
            client,
            [new LLMModel("declared", "chatgpt") { Name = "Declared" }],
            [
                new LLMModel("default", "chatgpt") { Name = "Default" },
                new LLMModel("live", "chatgpt")
                {
                    InputPrice = 0.001,
                    Fields = ModelMetadataFields.InputPrice,
                },
            ],
            [],
            false,
            new ResponsesWebSocketConnector());

        var seed = provider.SeedModels();
        var refreshed = await provider.ListModels(cancellationToken);

        _ = await Assert.That(string.Join(",", seed.Select(model => model.Id))).IsEqualTo("declared,default,live");
        _ = await Assert.That(string.Join(",", refreshed.Select(model => model.Id))).IsEqualTo("declared,live");
        _ = await Assert.That(refreshed.Single(model => model.Id == "live").InputPrice).IsEqualTo(0.001);
    }

    [Test]
    public async Task Chatgpt_call_omits_max_output_tokens(CancellationToken cancellationToken)
    {
        using var handler = new ChatGptCallHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new ChatGptProvider(new FakeOAuthTokenSource(), client, [], [], [], false, new ResponsesWebSocketConnector());
        var request = new LLMRequest
        {
            Model = "gpt-5.6-sol",
            MaxTokens = 4096,
            Messages = [LLMMessage.User("hello")],
        };

        await foreach (var item in provider.Call(request, cancellationToken))
        {
            _ = item;
        }

        using var document = JsonDocument.Parse(handler.Body);

        _ = await Assert.That(document.RootElement.TryGetProperty("max_output_tokens", out _)).IsFalse();
    }

    [Test]
    public async Task Duplicate_ids_are_rejected_at_construction() =>
        _ = await Assert.That(() => new ProviderRegistry(
            [new FakeProvider("p", true, null), new FakeProvider("p", true, null)],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["p"] = [],
            })).Throws<LLMProviderException>();

    [Test]
    public async Task Resolve_preserves_requested_alias_identity_and_canonical_slash_model_route()
    {
        var registry = new ProviderRegistry(
            [new FakeProvider("p", true, null)],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["p"] = [new LLMModel("vendor/model", "p")],
            });
        var catalog = new ModelAliasCatalog(
            registry,
            [new("preferred", "p/vendor/model", "primary", "system prompt", null)]);
        var router = new ModelRouter(registry, new ModelRouting(catalog, string.Empty));

        var selection = router.Resolve("preferred");

        _ = await Assert.That(selection.RequestedSelector.Value).IsEqualTo("preferred");
        _ = await Assert.That(selection.Alias?.Name).IsEqualTo("preferred");
        _ = await Assert.That(selection.CanonicalModel.Selector).IsEqualTo("p/vendor/model");
        _ = await Assert.That(selection.CanonicalBase).IsEqualTo("p/vendor/model");
    }

    [Test]
    public async Task Resolve_uses_default_alias_and_accepts_unlisted_alias_target()
    {
        var registry = new ProviderRegistry(
            [new FakeProvider("p", true, null)],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["p"] = [new LLMModel("listed", "p")],
            });
        var catalog = new ModelAliasCatalog(
            registry,
            [
                new("default", "p/not-listed", "primary", null, null),
                new("unlisted", "p/also-not-listed", "secondary", null, null),
            ]);
        var router = new ModelRouter(registry, new ModelRouting(catalog, "default"));

        var defaultSelection = router.Resolve(string.Empty);
        var unlistedSelection = router.Resolve("unlisted");

        _ = await Assert.That(defaultSelection.Alias?.Name).IsEqualTo("default");
        _ = await Assert.That(defaultSelection.CanonicalModel.Selector).IsEqualTo("p/not-listed");
        _ = await Assert.That(unlistedSelection.CanonicalModel.Selector).IsEqualTo("p/also-not-listed");
    }

    [Test]
    public async Task Resolve_rejects_disabled_alias()
    {
        var registry = new ProviderRegistry(
            [new FakeProvider("p", true, null)],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["p"] = [new LLMModel("listed", "p")],
            });
        var catalog = new ModelAliasCatalog(registry, [new("disabled", string.Empty, "primary", null, null)]);
        var router = new ModelRouter(registry, new ModelRouting(catalog, string.Empty));

        _ = await Assert.That(() => router.Resolve("disabled")).Throws<LLMProviderException>();
    }

    [Test]
    public async Task Replace_retargets_future_resolutions_while_captured_snapshot_keeps_old_alias()
    {
        var registry = new ProviderRegistry(
            [new FakeProvider("p", true, null)],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["p"] = [new LLMModel("old", "p"), new LLMModel("new", "p")],
            });
        var catalog = new ModelAliasCatalog(registry, [new("preferred", "p/old", "primary", null, null)]);
        var routing = new ModelRouting(catalog, string.Empty);
        var router = new ModelRouter(registry, routing);
        var beforeReplacement = router.Resolve("preferred");

        routing.Publish(routing.Prepare(string.Empty, [new("preferred", "p/new", "primary", null, null)]));
        var afterReplacement = router.Resolve("preferred");

        _ = await Assert.That(beforeReplacement.CanonicalModel.Selector).IsEqualTo("p/old");
        _ = await Assert.That(beforeReplacement.AliasSnapshot.Find("preferred")?.ModelString).IsEqualTo("p/old");
        _ = await Assert.That(afterReplacement.CanonicalModel.Selector).IsEqualTo("p/new");
        _ = await Assert.That(afterReplacement.AliasSnapshot.Find("preferred")?.ModelString).IsEqualTo("p/new");
    }

    [Test]
    public async Task Replacement_rejections_are_atomic_and_disallow_self_or_chained_aliases()
    {
        var registry = new ProviderRegistry(
            [new FakeProvider("p", true, null)],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["p"] = [new LLMModel("old", "p")],
            });
        var catalog = new ModelAliasCatalog(registry, [new("preferred", "p/old", "primary", null, null)]);

        _ = await Assert.That(() => new ModelAliasCatalog(
            registry,
            [new("self", "self", "primary", null, null)])).Throws<LLMProviderException>();
        _ = await Assert.That(() => new ModelAliasCatalog(
            registry,
            [new("first", "second", "primary", null, null), new("second", "p/old", "primary", null, null)]))
            .Throws<LLMProviderException>();
        var routing = new ModelRouting(catalog, string.Empty);
        _ = await Assert.That(() => routing.Prepare(
            string.Empty, [new("preferred", "preferred", "primary", null, null)]))
            .Throws<LLMProviderException>();
        _ = await Assert.That(routing.Capture().Aliases.Find("preferred")?.ModelString).IsEqualTo("p/old");
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

    [Test]
    public async Task Model_configuration_refresh_publishes_one_valid_generation_and_rejects_invalid_edits(
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-routing-refresh", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.yaml");

        try
        {
            const string initialConfiguration = """
                model: preferred
                model_aliases:
                  preferred:
                    model_string: p/old
                    usage: Primary
                """;
            await File.WriteAllTextAsync(path, initialConfiguration, cancellationToken);
            var registry = new ProviderRegistry(
            [new FakeProvider("p", true, null)],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["p"] = [new LLMModel("old", "p"), new LLMModel("new", "p")],
            });
            var configuration = Configuration.Load(path, Path.Combine(directory, "predefined_config.yaml"));
            var catalog = new ModelAliasCatalog(
                registry,
                configuration.ModelAliases.Select(alias => new ModelAliasDefinition(
                    alias.Key,
                    alias.Value.ModelString,
                    alias.Value.Usage,
                    alias.Value.AugmentSystemPrompt,
                    alias.Value.Icon is null ? null : ModelAliasIcon.Parse(alias.Value.Icon.Glyph, alias.Value.Icon.Color))));
            var routing = new ModelRouting(catalog, configuration.Model);
            var router = new ModelRouter(registry, routing);
            var coordinator = new ModelConfigurationCoordinator(configuration, routing, router);
            var initial = router.Resolve(string.Empty);

            const string refreshedConfiguration = """
                model: preferred
                model_aliases:
                  preferred:
                    model_string: p/new
                    usage: Updated primary
                """;
            await File.WriteAllTextAsync(path, refreshedConfiguration, cancellationToken);
            var refreshed = coordinator.Refresh();
            var afterRefresh = router.Resolve(string.Empty);
            await File.WriteAllTextAsync(path, "model_aliases: []\n", cancellationToken);
            var refused = Assert.Throws<InvalidDataException>(() => coordinator.Refresh());
            var afterRefusal = router.Resolve(string.Empty);

            _ = await Assert.That(initial.CanonicalModel.Selector).IsEqualTo("p/old");
            _ = await Assert.That(afterRefresh.CanonicalModel.Selector).IsEqualTo("p/new");
            _ = await Assert.That(afterRefresh.Alias?.Usage).IsEqualTo("Updated primary");
            _ = await Assert.That(refreshed.Revision).IsGreaterThan(initial.RoutingSnapshot.Revision);
            _ = await Assert.That(refused.Message).Contains("model_aliases must be a mapping");
            _ = await Assert.That(afterRefusal.CanonicalModel.Selector).IsEqualTo("p/new");
            _ = await Assert.That(afterRefusal.RoutingSnapshot.Revision).IsEqualTo(refreshed.Revision);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Model_preset_snapshot_overwrites_and_selection_overlays_later_aliases(
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-routing-preset", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.yaml");

        try
        {
            const string initialConfiguration = """
                model: preferred
                model_aliases:
                  preferred:
                    model_string: p/old
                    usage: Primary
                  empty:
                    model_string: ""
                    usage: Disabled
                """;
            await File.WriteAllTextAsync(path, initialConfiguration, cancellationToken);
            var registry = new ProviderRegistry(
            [new FakeProvider("p", true, null)],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["p"] = [new LLMModel("old", "p"), new LLMModel("new", "p"), new LLMModel("later", "p")],
            });
            var configuration = Configuration.Load(path, Path.Combine(directory, "predefined_config.yaml"));
            var catalog = new ModelAliasCatalog(
                registry,
                configuration.ModelAliases.Select(alias => new ModelAliasDefinition(
                    alias.Key,
                    alias.Value.ModelString,
                    alias.Value.Usage,
                    alias.Value.AugmentSystemPrompt,
                    alias.Value.Icon is null ? null : ModelAliasIcon.Parse(alias.Value.Icon.Glyph, alias.Value.Icon.Color))));
            var routing = new ModelRouting(catalog, configuration.Model);
            var router = new ModelRouter(registry, routing);
            var coordinator = new ModelConfigurationCoordinator(configuration, routing, router);

            var first = coordinator.SetPreset("Work", "preferred");
            var overwritten = coordinator.SetPreset("Work", "p/new");
            _ = coordinator.SetPreset("Work", "preferred");
            const string externallyEditedConfiguration = """
                model: p/new
                model_presets:
                  Work:
                    model: preferred
                    model_aliases:
                      empty: ""
                      high_llm: ""
                      low_llm: ""
                      medium_llm: ""
                      preferred: p/old
                      xhigh_llm: ""
                model_aliases:
                  preferred:
                    model_string: p/new
                    usage: Changed
                  empty:
                    model_string: p/later
                    usage: Enabled later
                  later:
                    model_string: p/later
                    usage: Added later
                """;
            await File.WriteAllTextAsync(path, externallyEditedConfiguration, cancellationToken);

            var selected = coordinator.SelectPreset("Work", "preferred", static _ => { }).ResolvedSelection.RoutingSnapshot;
            var reloaded = Configuration.Load(path, Path.Combine(directory, "predefined_config.yaml"));

            _ = await Assert.That(first.ModelAliases["empty"]).IsEmpty();
            _ = await Assert.That(first.ModelAliases.Keys).Contains("preferred");
            _ = await Assert.That(overwritten.Model).IsEqualTo("p/new");
            _ = await Assert.That(selected.ConfiguredDefaultSelector).IsEqualTo("preferred");
            _ = await Assert.That(selected.Aliases.Find("preferred")?.ModelString).IsEqualTo("p/old");
            _ = await Assert.That(selected.Aliases.Find("empty")?.ModelString).IsEmpty();
            _ = await Assert.That(selected.Aliases.Find("later")?.ModelString).IsEqualTo("p/later");
            _ = await Assert.That(reloaded.Model).IsEqualTo("preferred");
            _ = await Assert.That(reloaded.ModelAliases["later"].ModelString).IsEqualTo("p/later");
            _ = await Assert.That(reloaded.ModelPresets["Work"].Model).IsEqualTo("preferred");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Concurrent_preset_and_alias_mutations_publish_only_complete_generations(
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-routing-concurrency", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.yaml");

        try
        {
            const string initialConfiguration = """
                model: preferred
                model_aliases:
                  preferred:
                    model_string: p/old
                    usage: Primary
                """;
            await File.WriteAllTextAsync(path, initialConfiguration, cancellationToken);
            var registry = new ProviderRegistry(
            [new FakeProvider("p", true, null)],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["p"] = [new LLMModel("old", "p"), new LLMModel("new", "p")],
            });
            var configuration = Configuration.Load(path, Path.Combine(directory, "predefined_config.yaml"));
            var catalog = new ModelAliasCatalog(
                registry,
                configuration.ModelAliases.Select(alias => new ModelAliasDefinition(
                    alias.Key,
                    alias.Value.ModelString,
                    alias.Value.Usage,
                    alias.Value.AugmentSystemPrompt,
                    alias.Value.Icon is null ? null : ModelAliasIcon.Parse(alias.Value.Icon.Glyph, alias.Value.Icon.Color))));
            var routing = new ModelRouting(catalog, configuration.Model);
            var router = new ModelRouter(registry, routing);
            var coordinator = new ModelConfigurationCoordinator(configuration, routing, router);
            _ = coordinator.SetPreset("Work", "preferred");

            using var ready = new CountdownEvent(2);
            using var start = new ManualResetEventSlim();
            var select = Task.Run(
                () =>
                {
                    _ = ready.Signal();
                    start.Wait(cancellationToken);
                    return coordinator.SelectPreset("Work", "preferred", static _ => { }).ResolvedSelection;
                },
                cancellationToken);
            var configure = Task.Run(
                () =>
                {
                    _ = ready.Signal();
                    start.Wait(cancellationToken);
                    return coordinator.ConfigureAlias("preferred", "p/new");
                },
                cancellationToken);
            ready.Wait(cancellationToken);
            start.Set();
            await Task.WhenAll(select, configure);

            var persisted = Configuration.Load(path, Path.Combine(directory, "predefined_config.yaml"));
            var live = routing.Capture();

            _ = await Assert.That(live.ConfiguredDefaultSelector).IsEqualTo(persisted.Model);
            _ = await Assert.That(live.Aliases.Find("preferred")?.ModelString)
                .IsEqualTo(persisted.ModelAliases["preferred"].ModelString);
            _ = await Assert.That(router.Resolve("preferred").CanonicalModel.Selector)
                .IsEqualTo(persisted.ModelAliases["preferred"].ModelString);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Context_limits_refresh_alias_precedence_and_preset_rollback(bool rejectSelection)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-context-routing", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.yaml");
        try
        {
            const string initialConfiguration = """
                model: first
                context_limit: 100k
                model_aliases:
                  first:
                    model_string: p/model
                    usage: First
                    context_limit: 20%
                  second:
                    model_string: p/model
                    usage: Second
                """;
            await File.WriteAllTextAsync(path, initialConfiguration);
            var configuration = Configuration.Load(path, Path.Combine(directory, "predefined.yaml"));
            var registry = new ProviderRegistry(
                [new FakeProvider("p", true, null)],
                new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
                {
                    ["p"] = [new LLMModel("model", "p")],
                });
            var routing = new ModelRouting(new ModelAliasCatalog(registry, []), configuration.Model);
            var router = new ModelRouter(registry, routing);
            var coordinator = new ModelConfigurationCoordinator(configuration, routing, router);
            _ = coordinator.Refresh();
            var captured = router.Resolve("second");
            _ = coordinator.SetPreset("saved", "first");
            _ = coordinator.SetContextLimit(ContextSize.Parse("50k"));
            _ = await Assert.That(router.Resolve("first").ContextLimit).IsEqualTo(ContextSize.Parse("20%"));
            _ = await Assert.That(router.Resolve("second").ContextLimit).IsEqualTo(ContextSize.Parse("50k"));
            _ = await Assert.That(captured.ContextLimit).IsEqualTo(ContextSize.Parse("100k"));
            _ = await Assert.That(router.Resolve("first").CanonicalModel.Selector)
                .IsEqualTo(router.Resolve("second").CanonicalModel.Selector);
            if (rejectSelection)
            {
                _ = Assert.Throws<InvalidOperationException>(() => coordinator.SelectPreset(
                    "saved", "second", static _ => throw new InvalidOperationException("rejected")));
            }
            else
            {
                _ = coordinator.SelectPreset("saved", "second", static _ => { });
            }

            var expected = ContextSize.Parse(rejectSelection ? "50k" : "100k");
            _ = await Assert.That(router.Resolve("second").ContextLimit).IsEqualTo(expected);
            _ = await Assert.That(configuration.ContextLimit).IsEqualTo(expected);
            _ = await Assert.That(Configuration.Load(path, Path.Combine(directory, "predefined.yaml")).ContextLimit)
                .IsEqualTo(expected);
            _ = await Assert.That(router.Resolve("first").ContextLimit).IsEqualTo(ContextSize.Parse("20%"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class OpenRouterHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"id":"vendor/model"}]}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            var content = request.Content ?? throw new InvalidOperationException("OpenRouter request content is missing");
            RequestBody = await content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"choices":[{"index":0,"finish_reason":"stop","delta":{}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }

    private sealed class OpenAiModelsHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("temporarily unavailable", Encoding.UTF8, "text/plain"),
            });
        }
    }

    private sealed class ChatGptModelsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"models":[{"slug":"live","display_name":"Live","context_window":321,"visibility":"list"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class FakeOAuthTokenSource : IOAuthTokenSource
    {
        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<OAuthAccess> Token(CancellationToken cancellationToken) =>
            Task.FromResult(new OAuthAccess("token", "account"));
    }

    private sealed class ChatGptCallHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content ?? throw new InvalidOperationException("ChatGPT request content is missing");
            Body = await content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
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

        public IReadOnlyList<LLMModel> SeedModels() => [];

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
