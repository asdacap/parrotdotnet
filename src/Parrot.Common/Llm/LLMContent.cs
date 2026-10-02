namespace Parrot.Llm;

internal sealed record LLMContent
{
    private LLMContent(LLMContentKind kind, string text, string imagePath, string imageUrl, string mediaType, int imageWidth, int imageHeight)
    {
        Kind = kind;
        Text = text;
        ImagePath = imagePath;
        ImageUrl = imageUrl;
        MediaType = mediaType;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
    }

    public LLMContentKind Kind { get; }

    public string Text { get; }

    // Image bytes stay on disk; providers read them only while encoding a request.
    public string ImagePath { get; }

    // A public URL the provider can fetch instead of inline bytes; empty sends the bytes.
    public string ImageUrl { get; }

    public string MediaType { get; }

    public int ImageWidth { get; }

    public int ImageHeight { get; }

    public static LLMContent TextPart(string text) => new(LLMContentKind.Text, text, string.Empty, string.Empty, string.Empty, 0, 0);

    public static LLMContent ImageFile(string path, string mediaType, int width, int height) =>
        new(LLMContentKind.Image, string.Empty, path, string.Empty, mediaType, width, height);

    public static LLMContent ImageLink(string path, string url, string mediaType, int width, int height) =>
        new(LLMContentKind.Image, string.Empty, path, url, mediaType, width, height);

    public byte[] ReadImage() => File.ReadAllBytes(ImagePath);
}
