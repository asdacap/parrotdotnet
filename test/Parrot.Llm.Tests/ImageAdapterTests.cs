using System.Text.Json;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ImageAdapterTests
{
    [Test]
    public async Task Responses_encode_ordered_text_and_image_data_url(CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = "model",
            Messages = [LLMMessage.User([LLMContent.TextPart("before"), Image(), LLMContent.TextPart("after")])],
        };

        using var document = JsonDocument.Parse(ResponsesAdapter.Encode(request));
        var content = document.RootElement.GetProperty("input")[0].GetProperty("content");

        _ = await Assert.That(content.GetRawText()).IsEqualTo("[{\"type\":\"input_text\",\"text\":\"before\"},{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,AAEC\"},{\"type\":\"input_text\",\"text\":\"after\"}]");
        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task Chat_completions_encode_ordered_text_and_image_data_url(CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = "model",
            Messages = [LLMMessage.User([LLMContent.TextPart("before"), Image(), LLMContent.TextPart("after")])],
        };

        using var document = JsonDocument.Parse(ChatCompletionsAdapter.Encode(request));
        var content = document.RootElement.GetProperty("messages")[0].GetProperty("content");

        _ = await Assert.That(content.GetRawText()).IsEqualTo("[{\"type\":\"text\",\"text\":\"before\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AAEC\"}},{\"type\":\"text\",\"text\":\"after\"}]");
        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }

    private static LLMContent Image()
    {
        var path = Path.Combine(Path.GetTempPath(), $"parrot-image-{Guid.NewGuid():n}.png");
        File.WriteAllBytes(path, Convert.FromHexString("000102"));
        return LLMContent.ImageFile(path, "image/png", 1, 1);
    }
}
