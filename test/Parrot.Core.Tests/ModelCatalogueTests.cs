using Parrot.Config;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ModelCatalogueTests
{
    [Test]
    public async Task Standard_decoder_dedupes_and_defaults_the_name(CancellationToken cancellationToken)
    {
        var models = StandardModelDecoder.Instance.Decode(
            "p", """{"data":[{"id":"m1","context_window":100},{"id":"m1"},{"id":""}]}""");

        _ = await Assert.That(models.Count).IsEqualTo(1);
        _ = await Assert.That(models[0].Name).IsEqualTo("m1");
        _ = await Assert.That(models[0].ContextWindow).IsEqualTo(100);
        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task Standard_decoder_reads_rich_metadata_without_claiming_invalid_fields()
    {
        var models = StandardModelDecoder.Instance.Decode(
            "p",
            """{"data":[{"id":"rich","name":"Rich","max_input_tokens":0,"max_output_tokens":42,"input_cost_per_token":0,"cache_read_input_token_cost":0.00025,"output_cost_per_token":0.002,"supports_function_calling":false,"supports_reasoning":true,"supported_output_modalities":[],"reasoning_effort_levels":["low","high"],"default_reasoning_effort":"high"},{"id":"invalid","max_input_tokens":-1,"input_cost_per_token":-1,"supports_reasoning":"yes","supported_output_modalities":"text","supported_reasoning_efforts":"high"}]}""");
        var rich = models[0];
        var invalid = models[1];

        _ = await Assert.That(rich.Name).IsEqualTo("Rich");
        _ = await Assert.That(rich.ContextWindow).IsEqualTo(0);
        _ = await Assert.That(rich.MaxInputTokens).IsEqualTo(0);
        _ = await Assert.That(rich.MaxOutputTokens).IsEqualTo(42);
        _ = await Assert.That(rich.InputPrice).IsEqualTo(0);
        _ = await Assert.That(rich.CachedInputPrice).IsEqualTo(0.00025);
        _ = await Assert.That(rich.OutputPrice).IsEqualTo(0.002);
        _ = await Assert.That(rich.Capabilities.Tools).IsFalse();
        _ = await Assert.That(rich.Capabilities.Reasoning).IsTrue();
        _ = await Assert.That(rich.Capabilities.Output).IsEmpty();
        _ = await Assert.That(string.Join(",", rich.Capabilities.Variants.Select(variant => variant.Name)))
            .IsEqualTo("high,low");
        _ = await Assert.That(rich.Fields).IsEqualTo(
            ModelMetadataFields.Name
                | ModelMetadataFields.MaxOutputTokens
                | ModelMetadataFields.MaxInputTokens
                | ModelMetadataFields.InputPrice
                | ModelMetadataFields.CachedInputPrice
                | ModelMetadataFields.OutputPrice
                | ModelMetadataFields.Tools
                | ModelMetadataFields.Reasoning
                | ModelMetadataFields.Output
                | ModelMetadataFields.Variants);
        _ = await Assert.That(invalid.MaxInputTokens).IsEqualTo(0);
        _ = await Assert.That(invalid.Fields).IsEqualTo(ModelMetadataFields.None);
    }

    [Test]
    public async Task Litellm_decoder_tolerates_malformed_entries_and_conservatively_combines_deployments()
    {
        var models = LiteLlmModelInfoDecoder.Instance.Decode(
            "p",
            """{"data":[null,"bad",7,[],{"model_name":"served","model_info":{"name":"Served","max_input_tokens":128,"max_output_tokens":32,"input_cost_per_token":0.001,"cache_read_input_token_cost":0.0001,"output_cost_per_token":0.002,"supports_function_calling":true,"supports_reasoning":true,"supported_output_modalities":["text","image"],"supported_reasoning_efforts":["none","medium","xhigh","low","bogus","","low",null],"default_reasoning_effort":"medium"}},{"model_name":"served","model_info":{"max_input_tokens":64,"max_output_tokens":64,"input_cost_per_token":0.003,"cache_read_input_token_cost":0.0001,"output_cost_per_token":0.004,"supports_function_calling":false,"supports_reasoning":true,"supported_output_modalities":["text"],"reasoning_effort_levels":["minimal","low","medium","high","max","bogus"]}},{"model_name":"filtered","model_info":{"reasoning_effort_levels":["bogus","",null,"bogus"]}},{"model_name":"missing-info"},{"model_info":{"max_input_tokens":1}}]}""");
        var model = models.Single(candidate => candidate.Id == "served");
        var filtered = models.Single(candidate => candidate.Id == "filtered");

        _ = await Assert.That(models.Count).IsEqualTo(2);
        _ = await Assert.That(model.Name).IsEqualTo("served");
        _ = await Assert.That(model.Fields.HasFlag(ModelMetadataFields.Name)).IsFalse();
        _ = await Assert.That(model.ContextWindow).IsEqualTo(0);
        _ = await Assert.That(model.MaxInputTokens).IsEqualTo(64);
        _ = await Assert.That(model.MaxOutputTokens).IsEqualTo(32);
        _ = await Assert.That(model.Fields.HasFlag(ModelMetadataFields.InputPrice)).IsFalse();
        _ = await Assert.That(model.InputPrice).IsEqualTo(0);
        _ = await Assert.That(model.Fields.HasFlag(ModelMetadataFields.CachedInputPrice)).IsTrue();
        _ = await Assert.That(model.CachedInputPrice).IsEqualTo(0.0001);
        _ = await Assert.That(model.Fields.HasFlag(ModelMetadataFields.OutputPrice)).IsFalse();
        _ = await Assert.That(model.OutputPrice).IsEqualTo(0);
        _ = await Assert.That(model.Capabilities.Tools).IsFalse();
        _ = await Assert.That(model.Capabilities.Reasoning).IsTrue();
        _ = await Assert.That(string.Join(",", model.Capabilities.Output)).IsEqualTo("text");
        _ = await Assert.That(string.Join(",", model.Capabilities.Variants.Select(variant => variant.Name)))
            .IsEqualTo("medium,low");
        _ = await Assert.That(filtered.Fields.HasFlag(ModelMetadataFields.Variants)).IsTrue();
        _ = await Assert.That(filtered.Capabilities.Variants).IsEmpty();
    }

    [Test]
    public async Task Litellm_decoder_merges_configured_and_normalized_metadata_before_deployments()
    {
        var models = LiteLlmModelInfoDecoder.Instance.Decode(
            "p",
            """{"data":[{"model_name":"qwen38-27b","litellm_params":{"model_info":{"max_input_tokens":262144,"max_output_tokens":128,"supports_function_calling":true,"supports_reasoning":true,"reasoning_effort_levels":["low","medium","xhigh"]}},"model_info":{"max_input_tokens":null,"max_output_tokens":64,"supports_function_calling":false,"supports_reasoning":null}},{"model_name":"nested-only","litellm_params":{"model_info":{"max_input_tokens":1024,"supports_reasoning":true,"reasoning_effort_levels":["low"]}}},{"model_name":"normalized-only","litellm_params":{"model_info":[]},"model_info":{"max_input_tokens":2048,"supports_reasoning":false}},{"model_name":"malformed-normalized","litellm_params":{"model_info":{"max_input_tokens":4096}},"model_info":"bad"},{"model_name":"duplicate","litellm_params":{"model_info":{"max_input_tokens":8192,"supports_reasoning":true,"reasoning_effort_levels":["low","medium","xhigh"]}},"model_info":{"max_output_tokens":256}},{"model_name":"duplicate","litellm_params":{"model_info":{"max_input_tokens":4096,"supports_reasoning":true,"reasoning_effort_levels":["medium","xhigh"]}},"model_info":{"max_output_tokens":128}},{"model_name":"missing"},{"model_name":"malformed","litellm_params":{"model_info":null},"model_info":[]}]}""");
        var qwen = models.Single(model => model.Id == "qwen38-27b");
        var duplicate = models.Single(model => model.Id == "duplicate");

        _ = await Assert.That(string.Join(",", models.Select(model => model.Id)))
            .IsEqualTo("qwen38-27b,nested-only,normalized-only,malformed-normalized,duplicate");
        _ = await Assert.That(qwen.ContextWindow).IsEqualTo(0);
        _ = await Assert.That(qwen.MaxInputTokens).IsEqualTo(262144);
        _ = await Assert.That(qwen.MaxOutputTokens).IsEqualTo(64);
        _ = await Assert.That(qwen.Capabilities.Tools).IsFalse();
        _ = await Assert.That(qwen.Capabilities.Reasoning).IsTrue();
        _ = await Assert.That(string.Join(",", qwen.Capabilities.Variants.Select(variant => variant.Name)))
            .IsEqualTo("low,medium,xhigh");
        _ = await Assert.That(models.Single(model => model.Id == "nested-only").ContextWindow).IsEqualTo(0);
        _ = await Assert.That(models.Single(model => model.Id == "nested-only").MaxInputTokens).IsEqualTo(1024);
        _ = await Assert.That(models.Single(model => model.Id == "normalized-only").ContextWindow).IsEqualTo(0);
        _ = await Assert.That(models.Single(model => model.Id == "normalized-only").MaxInputTokens).IsEqualTo(2048);
        _ = await Assert.That(models.Single(model => model.Id == "normalized-only").Capabilities.Reasoning).IsFalse();
        _ = await Assert.That(models.Single(model => model.Id == "malformed-normalized").ContextWindow)
            .IsEqualTo(0);
        _ = await Assert.That(models.Single(model => model.Id == "malformed-normalized").MaxInputTokens)
            .IsEqualTo(4096);
        _ = await Assert.That(duplicate.ContextWindow).IsEqualTo(0);
        _ = await Assert.That(duplicate.MaxInputTokens).IsEqualTo(4096);
        _ = await Assert.That(duplicate.MaxOutputTokens).IsEqualTo(128);
        _ = await Assert.That(string.Join(",", duplicate.Capabilities.Variants.Select(variant => variant.Name)))
            .IsEqualTo("medium,xhigh");
    }

    [Test]
    public async Task Supplement_preserves_primary_membership_and_field_precedence_before_configuration()
    {
        var primary = StandardModelDecoder.Instance.Decode(
            "p",
            """{"data":[{"id":"served","max_output_tokens":0,"supports_function_calling":false},{"id":"primary-only","supports_reasoning":false}]}""");
        var supplemental = LiteLlmModelInfoDecoder.Instance.Decode(
            "p",
            """{"data":[{"model_name":"served","model_info":{"max_input_tokens":512,"max_output_tokens":64,"supports_function_calling":true,"supports_reasoning":true,"reasoning_effort_levels":["low","high"]}},{"model_name":"info-only","model_info":{"max_input_tokens":999}},{"model_name":"served","model_info":{"max_input_tokens":1024}}]}""");
        var configured = new LLMModel("served", "p")
        {
            ContextWindow = 256,
            InputPrice = 0.01,
            Capabilities = new ModelCapabilities(true, false, ["image"], []),
            Fields = ModelMetadataFields.ContextWindow
                | ModelMetadataFields.InputPrice
                | ModelMetadataFields.Tools
                | ModelMetadataFields.Reasoning
                | ModelMetadataFields.Output,
        };

        var supplemented = ModelCatalogue.Supplement(primary, supplemental);
        var merged = ModelCatalogue.Merge(supplemented, [configured], [], []);
        var served = merged.Single(model => model.Id == "served");

        _ = await Assert.That(string.Join(",", merged.Select(model => model.Id)))
            .IsEqualTo("primary-only,served");
        _ = await Assert.That(served.ContextWindow).IsEqualTo(256);
        _ = await Assert.That(served.MaxInputTokens).IsEqualTo(512);
        _ = await Assert.That(served.MaxOutputTokens).IsEqualTo(0);
        _ = await Assert.That(served.InputPrice).IsEqualTo(0.01);
        _ = await Assert.That(served.Capabilities.Tools).IsFalse();
        _ = await Assert.That(served.Capabilities.Reasoning).IsTrue();
        _ = await Assert.That(string.Join(",", served.Capabilities.Output)).IsEqualTo("image");
        _ = await Assert.That(string.Join(",", served.Capabilities.Variants.Select(variant => variant.Name)))
            .IsEqualTo("low,high");
    }

    [Test]
    public async Task Openrouter_decoder_reads_pricing_and_reasoning_variants()
    {
        var models = OpenRouterModelDecoder.Instance.Decode(
            "p",
            """{"data":[{"id":"x/y","context_length":9,"pricing":{"prompt":"0.001","input_cache_read":"0.00025","completion":"0.002"},"top_provider":{"max_completion_tokens":42},"reasoning":{"supported_efforts":["low","high"],"default_effort":"high"}}]}""");

        _ = await Assert.That(models[0].InputPrice).IsEqualTo(0.001);
        _ = await Assert.That(models[0].CachedInputPrice).IsEqualTo(0.00025);
        _ = await Assert.That(models[0].OutputPrice).IsEqualTo(0.002);
        _ = await Assert.That(models[0].MaxOutputTokens).IsEqualTo(42);
        _ = await Assert.That(models[0].Capabilities.Reasoning).IsTrue();
        _ = await Assert.That(models[0].Capabilities.Variants[0].Name).IsEqualTo("high");
    }

    [Test]
    public async Task Openrouter_endpoint_explicit_zero_price_overrides_configured_price()
    {
        var configured = new LLMModel("m", "p")
        {
            InputPrice = 0.01,
            CachedInputPrice = 0.005,
            OutputPrice = 0.02,
            Fields = ModelMetadataFields.InputPrice | ModelMetadataFields.CachedInputPrice | ModelMetadataFields.OutputPrice,
        };
        var fetched = OpenRouterModelDecoder.Instance.Decode(
            "p",
            """{"data":[{"id":"m","pricing":{"prompt":"0","input_cache_read":"0","completion":"0"}}]}""");

        var merged = ModelCatalogue.Merge(fetched, [configured], [], []).Single();

        _ = await Assert.That(merged.InputPrice).IsEqualTo(0);
        _ = await Assert.That(merged.CachedInputPrice).IsEqualTo(0);
        _ = await Assert.That(merged.OutputPrice).IsEqualTo(0);
    }

    [Test]
    [Arguments("-0.1")]
    [Arguments("NaN")]
    [Arguments("Infinity")]
    public async Task Openrouter_invalid_endpoint_price_does_not_override_configured_price(string price)
    {
        var configured = new LLMModel("m", "p")
        {
            InputPrice = 0.01,
            CachedInputPrice = 0.005,
            Fields = ModelMetadataFields.InputPrice | ModelMetadataFields.CachedInputPrice,
        };
        var fetched = OpenRouterModelDecoder.Instance.Decode(
            "p",
            "{\"data\":[{\"id\":\"m\",\"pricing\":{\"prompt\":\"" + price + "\"}}]}");

        var merged = ModelCatalogue.Merge(fetched, [configured], [], []).Single();

        _ = await Assert.That(merged.InputPrice).IsEqualTo(0.01);
        _ = await Assert.That(merged.CachedInputPrice).IsEqualTo(0.005);
    }

    [Test]
    public async Task Explicit_endpoint_non_reasoning_suppresses_configured_variants()
    {
        var configured = new LLMModel("m", "p")
        {
            Capabilities = new ModelCapabilities(false, true, ["text"], [new ModelVariant("high", "high")]),
            Fields = ModelMetadataFields.Reasoning | ModelMetadataFields.Variants,
        };
        var fetched = StandardModelDecoder.Instance.Decode(
            "p", """{"data":[{"id":"m","supports_reasoning":false}]}""");

        var merged = ModelCatalogue.Merge(fetched, [configured], [], []).Single();

        _ = await Assert.That(merged.Capabilities.Reasoning).IsFalse();
        _ = await Assert.That(merged.Capabilities.Variants).IsEmpty();
        _ = await Assert.That(merged.Fields.HasFlag(ModelMetadataFields.Variants)).IsTrue();
    }

    [Test]
    public async Task Endpoint_reasoning_container_without_fields_does_not_clear_configured_metadata()
    {
        var configured = new LLMModel("m", "p")
        {
            Capabilities = new ModelCapabilities(false, true, ["text"], [new ModelVariant("high", "high")]),
            Fields = ModelMetadataFields.Reasoning | ModelMetadataFields.Variants,
        };
        var fetched = OpenRouterModelDecoder.Instance.Decode("p", """{"data":[{"id":"m","reasoning":{}}]}""");

        var merged = ModelCatalogue.Merge(fetched, [configured], [], []).Single();

        _ = await Assert.That(merged.Capabilities.Reasoning).IsTrue();
        _ = await Assert.That(merged.Capabilities.Variants).HasSingleItem();
    }

    [Test]
    public async Task Merge_prioritizes_endpoint_fields_and_fills_only_omitted_fields_from_configuration()
    {
        var configured = new LLMModel("served", "p")
        {
            Name = "Configured",
            ContextWindow = 500,
            MaxOutputTokens = 100,
            InputPrice = 0.01,
            CachedInputPrice = 0.005,
            OutputPrice = 0.02,
            Capabilities = new ModelCapabilities(true, true, ["image"], [new ModelVariant("high", "high")]),
            Fields = ModelMetadataFields.Name
                | ModelMetadataFields.ContextWindow
                | ModelMetadataFields.MaxOutputTokens
                | ModelMetadataFields.InputPrice
                | ModelMetadataFields.CachedInputPrice
                | ModelMetadataFields.OutputPrice
                | ModelMetadataFields.Tools
                | ModelMetadataFields.Reasoning
                | ModelMetadataFields.Output
                | ModelMetadataFields.Variants,
        };
        var fetched = new LLMModel("served", "p")
        {
            Name = "Endpoint",
            ContextWindow = 0,
            InputPrice = 0,
            CachedInputPrice = 0,
            Capabilities = new ModelCapabilities(false, false, [], []),
            Fields = ModelMetadataFields.Name
                | ModelMetadataFields.ContextWindow
                | ModelMetadataFields.InputPrice
                | ModelMetadataFields.CachedInputPrice
                | ModelMetadataFields.Tools
                | ModelMetadataFields.Reasoning
                | ModelMetadataFields.Output
                | ModelMetadataFields.Variants,
        };

        var merged = ModelCatalogue.Merge([fetched], [configured], [], []).Single();

        _ = await Assert.That(merged.Name).IsEqualTo("Endpoint");
        _ = await Assert.That(merged.ContextWindow).IsEqualTo(0);
        _ = await Assert.That(merged.MaxOutputTokens).IsEqualTo(100);
        _ = await Assert.That(merged.InputPrice).IsEqualTo(0);
        _ = await Assert.That(merged.CachedInputPrice).IsEqualTo(0);
        _ = await Assert.That(merged.OutputPrice).IsEqualTo(0.02);
        _ = await Assert.That(merged.Capabilities.Tools).IsFalse();
        _ = await Assert.That(merged.Capabilities.Reasoning).IsFalse();
        _ = await Assert.That(merged.Capabilities.Output).IsEmpty();
        _ = await Assert.That(merged.Capabilities.Variants).IsEmpty();
    }

    [Test]
    public async Task Merge_uses_declared_then_default_fallbacks_and_preserves_membership_lifecycle()
    {
        var declared = new[]
        {
            new LLMModel("served", "p")
            {
                Name = "Declared",
                Fields = ModelMetadataFields.Name,
            },
            new LLMModel("declared", "p"),
        };
        var defaults = new[]
        {
            new LLMModel("served", "p")
            {
                ContextWindow = 500,
                Fields = ModelMetadataFields.ContextWindow,
            },
            new LLMModel("absent", "p") { Name = "Absent", Fields = ModelMetadataFields.Name },
        };
        var fetched = new[]
        {
            new LLMModel("served", "p"),
            new LLMModel("fresh", "p") { Name = "Fresh", Fields = ModelMetadataFields.Name },
        };

        var merged = ModelCatalogue.Merge(fetched, declared, defaults, []);
        var ids = string.Join(",", merged.Select(model => model.Id));
        var served = merged.Single(model => model.Id == "served");

        _ = await Assert.That(ids).IsEqualTo("declared,fresh,served");
        _ = await Assert.That(served.Name).IsEqualTo("Declared");
        _ = await Assert.That(served.ContextWindow).IsEqualTo(500);
    }

    [Test]
    public async Task Merge_preserves_external_membership_and_uses_all_four_metadata_levels()
    {
        var external = new LLMModel("shared", "p")
        {
            Name = "External",
            ContextWindow = 100,
            MaxInputTokens = 80,
            MaxOutputTokens = 60,
            InputPrice = 0.01,
            CachedInputPrice = 0.001,
            OutputPrice = 0.02,
            Capabilities = new ModelCapabilities(true, true, ["text"], [new ModelVariant("low", "low")]),
            Fields = ModelMetadataFields.Name
                | ModelMetadataFields.ContextWindow
                | ModelMetadataFields.MaxInputTokens
                | ModelMetadataFields.MaxOutputTokens
                | ModelMetadataFields.InputPrice
                | ModelMetadataFields.CachedInputPrice
                | ModelMetadataFields.OutputPrice
                | ModelMetadataFields.Tools
                | ModelMetadataFields.Reasoning
                | ModelMetadataFields.Output
                | ModelMetadataFields.Variants,
        };
        var defaults = new LLMModel("shared", "p")
        {
            Name = "Default",
            ContextWindow = 200,
            MaxInputTokens = 160,
            MaxOutputTokens = 120,
            InputPrice = 0.02,
            CachedInputPrice = 0.002,
            OutputPrice = 0.04,
            Capabilities = new ModelCapabilities(false, false, [], []),
            Fields = external.Fields,
        };
        var declared = new LLMModel("shared", "p")
        {
            Name = "Declared",
            ContextWindow = 300,
            MaxInputTokens = 240,
            MaxOutputTokens = 180,
            InputPrice = 0.03,
            CachedInputPrice = 0.003,
            OutputPrice = 0.06,
            Capabilities = new ModelCapabilities(true, true, ["image"], [new ModelVariant("high", "high")]),
            Fields = external.Fields,
        };
        var fetched = new LLMModel("shared", "p")
        {
            Name = "Live",
            ContextWindow = 0,
            MaxInputTokens = 0,
            MaxOutputTokens = 0,
            InputPrice = 0,
            CachedInputPrice = 0,
            OutputPrice = 0,
            Capabilities = new ModelCapabilities(false, false, [], []),
            Fields = external.Fields,
        };

        var seeded = ModelCatalogue.Merge(null, [declared], [defaults, new LLMModel("default-only", "p")], [external, new LLMModel("external-only", "p")]);
        var defaulted = ModelCatalogue.Merge(null, [], [defaults], [external]);
        var refreshed = ModelCatalogue.Merge([fetched, new LLMModel("live-only", "p"), fetched], [declared], [defaults, new LLMModel("default-only", "p")], [external, new LLMModel("external-only", "p")]);
        var seededShared = seeded.Single(item => item.Id == "shared");
        var model = refreshed.Single(item => item.Id == "shared");

        _ = await Assert.That(string.Join(",", seeded.Select(item => item.Id)))
            .IsEqualTo("default-only,external-only,shared");
        _ = await Assert.That(seededShared.Name).IsEqualTo("Declared");
        _ = await Assert.That(seededShared.ContextWindow).IsEqualTo(300);
        _ = await Assert.That(seededShared.MaxInputTokens).IsEqualTo(240);
        _ = await Assert.That(defaulted.Single().Name).IsEqualTo("Default");
        _ = await Assert.That(defaulted.Single().ContextWindow).IsEqualTo(200);
        _ = await Assert.That(defaulted.Single().MaxInputTokens).IsEqualTo(160);
        _ = await Assert.That(string.Join(",", refreshed.Select(item => item.Id)))
            .IsEqualTo("external-only,live-only,shared");
        _ = await Assert.That(model.Name).IsEqualTo("Live");
        _ = await Assert.That(model.ContextWindow).IsEqualTo(0);
        _ = await Assert.That(model.MaxInputTokens).IsEqualTo(0);
        _ = await Assert.That(model.MaxOutputTokens).IsEqualTo(0);
        _ = await Assert.That(model.InputPrice).IsEqualTo(0);
        _ = await Assert.That(model.CachedInputPrice).IsEqualTo(0);
        _ = await Assert.That(model.OutputPrice).IsEqualTo(0);
        _ = await Assert.That(model.Capabilities.Tools).IsFalse();
        _ = await Assert.That(model.Capabilities.Reasoning).IsFalse();
        _ = await Assert.That(model.Capabilities.Output).IsEmpty();
        _ = await Assert.That(model.Capabilities.Variants).IsEmpty();
    }

    [Test]
    public async Task Merge_with_empty_external_preserves_existing_membership_lifecycle()
    {
        LLMModel[] defaults = [new("default", "p")];
        LLMModel[] declared = [new("declared", "p")];

        var seeded = ModelCatalogue.Merge(null, declared, defaults, []);
        var refreshed = ModelCatalogue.Merge([new LLMModel("live", "p")], declared, defaults, []);

        _ = await Assert.That(string.Join(",", seeded.Select(item => item.Id))).IsEqualTo("declared,default");
        _ = await Assert.That(string.Join(",", refreshed.Select(item => item.Id))).IsEqualTo("declared,live");
    }

    [Test]
    public async Task Standard_decoder_synthetic_capabilities_do_not_override_configured_capabilities()
    {
        var configured = new LLMModel("m", "p")
        {
            Capabilities = new ModelCapabilities(false, true, ["image"], [new ModelVariant("high", "high")]),
            Fields = ModelMetadataFields.Tools
                | ModelMetadataFields.Reasoning
                | ModelMetadataFields.Output
                | ModelMetadataFields.Variants,
        };
        var fetched = StandardModelDecoder.Instance.Decode("p", """{"data":[{"id":"m"}]}""");

        var merged = ModelCatalogue.Merge(fetched, [configured], [], []).Single();

        _ = await Assert.That(merged.Capabilities.Tools).IsFalse();
        _ = await Assert.That(merged.Capabilities.Reasoning).IsTrue();
        _ = await Assert.That(string.Join(",", merged.Capabilities.Output)).IsEqualTo("image");
        _ = await Assert.That(merged.Capabilities.Variants).HasSingleItem();
    }

    [Test]
    public async Task Configured_defaults_preserve_variant_insertion_order()
    {
        var model = new ModelConfig
        {
            Variants = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["high"] = "high",
                ["low"] = "low",
                ["medium"] = "medium",
            },
        };

        var defaults = ProviderModels.ReadDefaults(
            "p",
            new Dictionary<string, ModelConfig>(StringComparer.Ordinal) { ["m"] = model });
        var variants = string.Join(",", defaults.Single().Capabilities.Variants.Select(variant => variant.Name));

        _ = await Assert.That(variants).IsEqualTo("high,low,medium");
    }
}
