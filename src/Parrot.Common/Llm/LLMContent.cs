namespace Parrot.Llm;

internal sealed record LLMContent
{
    private LLMContent(LLMContentKind kind, string text, byte[] image, string mediaType)
    {
        Kind = kind;
        Text = text;
        Image = image;
        MediaType = mediaType;
    }

    public LLMContentKind Kind { get; }

    public string Text { get; }

    public byte[] Image { get; }

    public string MediaType { get; }

    public static LLMContent TextPart(string text) => new(LLMContentKind.Text, text, [], string.Empty);

    public static LLMContent ImagePart(byte[] image, string mediaType) =>
        new(LLMContentKind.Image, string.Empty, image, mediaType);
}
