using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ModelsDevCatalogueDecoderTests
{
    [Test]
    public async Task Decoder_reads_model_metadata_prices_and_supported_efforts()
    {
        var catalogue = ModelsDevCatalogueDecoder.Decode(
            """
            {
              "openai": {
                "id": "openai",
                "unknown": true,
                "models": {
                  "first": {
                    "id": "gpt-example",
                    "name": "Example",
                    "limit": { "context": 400000, "input": 272000, "output": 128000 },
                    "cost": { "input": 5, "cache_read": 0, "output": 30 },
                    "tool_call": false,
                    "reasoning": true,
                    "modalities": { "output": ["text", "image", 7] },
                    "reasoning_options": [
                      { "type": "toggle" },
                      { "type": "effort", "values": [null, "high", "bogus", "low", "high"] }
                    ]
                  }
                }
              }
            }
            """);
        var model = catalogue["openai"].Single();

        _ = await Assert.That(model.Id).IsEqualTo("gpt-example");
        _ = await Assert.That(model.ProviderId).IsEqualTo("openai");
        _ = await Assert.That(model.Name).IsEqualTo("Example");
        _ = await Assert.That(model.ContextWindow).IsEqualTo(400_000);
        _ = await Assert.That(model.MaxInputTokens).IsEqualTo(272_000);
        _ = await Assert.That(model.MaxOutputTokens).IsEqualTo(128_000);
        _ = await Assert.That(model.InputPrice).IsEqualTo(0.000005);
        _ = await Assert.That(model.CachedInputPrice).IsEqualTo(0);
        _ = await Assert.That(model.OutputPrice).IsEqualTo(0.00003);
        _ = await Assert.That(model.Capabilities.Tools).IsFalse();
        _ = await Assert.That(model.Capabilities.Reasoning).IsTrue();
        _ = await Assert.That(string.Join(",", model.Capabilities.Output)).IsEqualTo("text,image");
        _ = await Assert.That(string.Join(",", model.Capabilities.Variants.Select(item => item.Name)))
            .IsEqualTo("high,low");
        _ = await Assert.That(model.Fields).IsEqualTo(
            ModelMetadataFields.Name
            | ModelMetadataFields.ContextWindow
            | ModelMetadataFields.MaxInputTokens
            | ModelMetadataFields.MaxOutputTokens
            | ModelMetadataFields.InputPrice
            | ModelMetadataFields.CachedInputPrice
            | ModelMetadataFields.OutputPrice
            | ModelMetadataFields.Tools
            | ModelMetadataFields.Reasoning
            | ModelMetadataFields.Output
            | ModelMetadataFields.Variants);
    }

    [Test]
    public async Task Decoder_skips_invalid_records_and_keeps_first_duplicate_id()
    {
        var catalogue = ModelsDevCatalogueDecoder.Decode(
            """
            {
              "invalid": [],
              "first": {
                "id": "duplicate",
                "models": {
                  "one": { "id": "first", "name": "First" },
                  "two": { "id": "first", "name": "Second" },
                  "three": { "id": "", "name": "Empty" },
                  "four": [],
                  "five": {
                    "id": "invalid-values",
                    "limit": { "context": -1, "input": 1.5, "output": 99999999999999999999 },
                    "cost": { "input": -1, "cache_read": "NaN", "output": "Infinity" },
                    "tool_call": "true",
                    "reasoning": null,
                    "modalities": { "output": "text" },
                    "reasoning_options": [{ "type": "effort", "values": "high" }]
                  }
                }
              },
              "second": {
                "id": "duplicate",
                "models": { "later": { "id": "later" } }
              }
            }
            """);
        var models = catalogue["duplicate"];
        var first = models.Single(model => model.Id == "first");
        var invalid = models.Single(model => model.Id == "invalid-values");

        _ = await Assert.That(catalogue.Count).IsEqualTo(1);
        _ = await Assert.That(models.Count).IsEqualTo(2);
        _ = await Assert.That(first.Name).IsEqualTo("First");
        _ = await Assert.That(invalid.Fields).IsEqualTo(ModelMetadataFields.None);
        _ = await Assert.That(invalid.Name).IsEqualTo("invalid-values");
        _ = await Assert.That(invalid.Capabilities.Output).IsEmpty();
    }

    [Test]
    [Arguments("[]")]
    [Arguments("null")]
    [Arguments("not-json")]
    public async Task Decoder_rejects_malformed_catalogue_roots(string json) =>
        _ = await Assert.That(() => ModelsDevCatalogueDecoder.Decode(json)).Throws<Exception>();
}
