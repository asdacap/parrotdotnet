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
    public async Task Openrouter_decoder_reads_pricing_and_reasoning_variants()
    {
        var models = OpenRouterModelDecoder.Instance.Decode(
            "p",
            """{"data":[{"id":"x/y","context_length":9,"pricing":{"prompt":"0.001","completion":"0.002"},"top_provider":{"max_completion_tokens":42},"reasoning":{"supported_efforts":["low","high"],"default_effort":"high"}}]}""");

        _ = await Assert.That(models[0].InputPrice).IsEqualTo(0.001);
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
            OutputPrice = 0.02,
            Fields = ModelMetadataFields.InputPrice | ModelMetadataFields.OutputPrice,
        };
        var fetched = OpenRouterModelDecoder.Instance.Decode(
            "p",
            """{"data":[{"id":"m","pricing":{"prompt":"0","completion":"0"}}]}""");

        var merged = ModelCatalogue.Merge(fetched, [configured], []).Single();

        _ = await Assert.That(merged.InputPrice).IsEqualTo(0);
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
            Fields = ModelMetadataFields.InputPrice,
        };
        var fetched = OpenRouterModelDecoder.Instance.Decode(
            "p",
            "{\"data\":[{\"id\":\"m\",\"pricing\":{\"prompt\":\"" + price + "\"}}]}");

        var merged = ModelCatalogue.Merge(fetched, [configured], []).Single();

        _ = await Assert.That(merged.InputPrice).IsEqualTo(0.01);
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

        var merged = ModelCatalogue.Merge(fetched, [configured], []).Single();

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
            OutputPrice = 0.02,
            Capabilities = new ModelCapabilities(true, true, ["image"], [new ModelVariant("high", "high")]),
            Fields = ModelMetadataFields.Name
                | ModelMetadataFields.ContextWindow
                | ModelMetadataFields.MaxOutputTokens
                | ModelMetadataFields.InputPrice
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
            Capabilities = new ModelCapabilities(false, false, [], []),
            Fields = ModelMetadataFields.Name
                | ModelMetadataFields.ContextWindow
                | ModelMetadataFields.InputPrice
                | ModelMetadataFields.Tools
                | ModelMetadataFields.Reasoning
                | ModelMetadataFields.Output
                | ModelMetadataFields.Variants,
        };

        var merged = ModelCatalogue.Merge([fetched], [configured], []).Single();

        _ = await Assert.That(merged.Name).IsEqualTo("Endpoint");
        _ = await Assert.That(merged.ContextWindow).IsEqualTo(0);
        _ = await Assert.That(merged.MaxOutputTokens).IsEqualTo(100);
        _ = await Assert.That(merged.InputPrice).IsEqualTo(0);
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

        var merged = ModelCatalogue.Merge(fetched, declared, defaults);
        var ids = string.Join(",", merged.Select(model => model.Id));
        var served = merged.Single(model => model.Id == "served");

        _ = await Assert.That(ids).IsEqualTo("declared,fresh,served");
        _ = await Assert.That(served.Name).IsEqualTo("Declared");
        _ = await Assert.That(served.ContextWindow).IsEqualTo(500);
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

        var merged = ModelCatalogue.Merge(fetched, [configured], []).Single();

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
