using Parrot.Llm;

namespace Parrot.Store;

internal sealed record CompactionGroupArtifactContent(
    string Kind,
    string Text,
    byte[] Image,
    string MediaType)
{
    public static CompactionGroupArtifactContent From(LLMContent content) =>
        new(content.Kind.ToString(), content.Text, content.Image, content.MediaType);
}
