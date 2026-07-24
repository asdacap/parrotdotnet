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
    public async Task Merge_keeps_declared_overlays_metadata_and_drops_unserved_defaults()
    {
        var declared = new[] { new LLMModel("declared", "p") };
        var defaults = new[]
        {
            new LLMModel("served", "p") { Name = "Served", ContextWindow = 500 },
            new LLMModel("absent", "p") { Name = "Absent" },
        };
        var fetched = new[] { new LLMModel("served", "p"), new LLMModel("fresh", "p") { Name = "Fresh" } };

        var merged = ModelCatalogue.Merge(fetched, declared, defaults);
        var ids = string.Join(",", merged.Select(model => model.Id));
        var served = merged.Single(model => model.Id == "served");

        _ = await Assert.That(ids).IsEqualTo("declared,fresh,served");
        _ = await Assert.That(served.ContextWindow).IsEqualTo(500);
    }
}
