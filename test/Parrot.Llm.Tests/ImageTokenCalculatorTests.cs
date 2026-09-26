using Parrot.Auth;
using Parrot.Config;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ImageTokenCalculatorTests
{
    [Test]
    [Arguments("gpt-6-astra", 400, 400, 203L)]
    [Arguments("gpt-6-astra", 1200, 1200, 1733L)]
    [Arguments("gpt-6-astra", 2048, 2048, 4916L)]
    [Arguments("gpt-6-astra", 1, 1, 2L)]
    [Arguments("gpt-6-astra", 31, 32, 2L)]
    [Arguments("gpt-6-astra", 32, 32, 2L)]
    [Arguments("gpt-6-astra", 33, 32, 3L)]
    [Arguments("gpt-6-astra", 4096, 512, 2458L)]
    [Arguments("gpt-5.6-luna", 2048, 2048, 4916L)]
    [Arguments("gpt-5.6-sol", 400, 400, 203L)]
    [Arguments("gpt-5.6-terra", 400, 400, 203L)]
    [Arguments("gpt-5.5", 4096, 4096, 12000L)]
    [Arguments("gpt-5.4", 2048, 2048, 3000L)]
    [Arguments("gpt-5.4", 2048, 1536, 2942L)]
    [Arguments("gpt-5.4", 1536, 2048, 2942L)]
    [Arguments("gpt-5.5", 12000, 32, 226L)]
    [Arguments("gpt-6-astra", 65536, 1, 2458L)]
    [Arguments("gpt-5.4-mini", 2048, 2048, 3000L)]
    [Arguments("gpt-5.4-nano", 2048, 2048, 3000L)]
    [Arguments("gpt-5.2", 4096, 2048, 2458L)]
    [Arguments("gpt-4.1-mini", 1024, 1024, 1659L)]
    [Arguments("gpt-4.1-mini-2025-04-14", 1024, 1024, 1659L)]
    [Arguments("gpt-4o", 1024, 1024, 765L)]
    [Arguments("gpt-4o", 400, 400, 255L)]
    [Arguments("gpt-4o", 4096, 1024, 765L)]
    [Arguments("gpt-4.1", 1024, 2048, 1105L)]
    [Arguments("gpt-5.1", 1024, 1024, 630L)]
    [Arguments("gpt-4o-mini", 1024, 1024, 25501L)]
    [Arguments("gpt-4.1-mini-unknown", 400, 400, 4096L)]
    [Arguments("gpt-6-astra-special", 400, 400, 4096L)]
    public async Task Calculates_documented_auto_detail(string model, int width, int height, long expected)
    {
        var content = LLMContent.ImageFile("unused.png", "image/png", width, height);
        var actual = OpenAiImageTokenCalculator.Instance.CalculateImageTokens(new(model, "openai"), content);
        _ = await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task Invalid_images_and_unknown_providers_retain_fallback()
    {
        var model = new LLMModel("gpt-6-astra", "openai");
        var invalid = LLMContent.ImageFile("unused.png", "image/png", 0, 0);
        _ = await Assert.That(OpenAiImageTokenCalculator.Instance.CalculateImageTokens(model, invalid)).IsEqualTo(4096L);
        _ = await Assert.That(OpenAiImageTokenCalculator.Instance.CalculateImageTokens(model, LLMContent.ImageFile("unused.png", "image/png", 400, 0))).IsEqualTo(4096L);
        ILLMProvider unknown = new UnusedProvider();
        _ = await Assert.That(unknown.CalculateImageTokens(model, invalid)).IsEqualTo(4096L);
    }

    [Test]
    [Arguments("openai", 203L)]
    [Arguments("chatgpt", 203L)]
    [Arguments("generic", 4096L)]
    [Arguments("openrouter", 4096L)]
    public async Task Composition_and_retry_preserve_provider_calculation(string providerId, long expected)
    {
        using var client = new HttpClient();
        var context = new ProviderBuildContext(
            providerId,
            new ProviderConfig { BaseUrl = "https://example.test/v1" },
            new InMemoryCredentialStore(),
            client,
            new UnusedBrowser(),
            []);
        var built = ProviderImplementations.Resolve(providerId).Build(context);
        ILLMProvider provider = new RetryingProvider(built.Provider);
        var content = LLMContent.ImageFile("unused.png", "image/png", 400, 400);
        _ = await Assert.That(provider.CalculateImageTokens(new("gpt-6-astra", providerId), content)).IsEqualTo(expected);
    }

    private sealed class UnusedBrowser : IBrowserOpener
    {
        public Task Open(string url, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
