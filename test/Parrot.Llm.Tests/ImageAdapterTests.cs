using System.Text.Json;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ImageAdapterTests
{
    private const string Link = "https://minio.example.com/parrot-images/parrot/abc.png";

    [Test]
    [Arguments(false, "data:image/png;base64,AAEC")]
    [Arguments(true, Link)]
    public async Task Responses_encode_ordered_text_and_image_reference(bool linked, string expectedReference)
    {
        var request = new LLMRequest
        {
            Model = "model",
            Messages = [LLMMessage.User([LLMContent.TextPart("before"), Image(linked), LLMContent.TextPart("after")])],
        };

        using var document = JsonDocument.Parse(ResponsesAdapter.Encode(request));
        var content = document.RootElement.GetProperty("input")[0].GetProperty("content");

        _ = await Assert.That(content.GetRawText()).IsEqualTo($"[{{\"type\":\"input_text\",\"text\":\"before\"}},{{\"type\":\"input_image\",\"image_url\":\"{expectedReference}\"}},{{\"type\":\"input_text\",\"text\":\"after\"}}]");
    }

    [Test]
    [Arguments(false, "data:image/png;base64,AAEC")]
    [Arguments(true, Link)]
    public async Task Chat_completions_encode_ordered_text_and_image_reference(bool linked, string expectedReference)
    {
        var request = new LLMRequest
        {
            Model = "model",
            Messages = [LLMMessage.User([LLMContent.TextPart("before"), Image(linked), LLMContent.TextPart("after")])],
        };

        using var document = JsonDocument.Parse(ChatCompletionsAdapter.Encode(request));
        var content = document.RootElement.GetProperty("messages")[0].GetProperty("content");

        _ = await Assert.That(content.GetRawText()).IsEqualTo($"[{{\"type\":\"text\",\"text\":\"before\"}},{{\"type\":\"image_url\",\"image_url\":{{\"url\":\"{expectedReference}\"}}}},{{\"type\":\"text\",\"text\":\"after\"}}]");
    }

    private static LLMContent Image(bool linked)
    {
        var path = Path.Combine(Path.GetTempPath(), $"parrot-image-{Guid.NewGuid():n}.png");
        File.WriteAllBytes(path, Convert.FromHexString("000102"));
        return linked
            ? LLMContent.ImageLink(path, Link, "image/png", 1, 1)
            : LLMContent.ImageFile(path, "image/png", 1, 1);
    }
}
