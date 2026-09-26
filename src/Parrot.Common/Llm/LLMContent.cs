namespace Parrot.Llm;

internal sealed record LLMContent
{
    private LLMContent(LLMContentKind kind, string text, string imagePath, string mediaType, int imageWidth, int imageHeight)
    {
        Kind = kind;
        Text = text;
        ImagePath = imagePath;
        MediaType = mediaType;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
    }

    public LLMContentKind Kind { get; }

    public string Text { get; }

    // Image bytes stay on disk; providers read them only while encoding a request.
    public string ImagePath { get; }

    public string MediaType { get; }

    public int ImageWidth { get; }

    public int ImageHeight { get; }

    public static LLMContent TextPart(string text) => new(LLMContentKind.Text, text, string.Empty, string.Empty, 0, 0);

    public static LLMContent ImageFile(string path, string mediaType, int width, int height) =>
        new(LLMContentKind.Image, string.Empty, path, mediaType, width, height);

    public byte[] ReadImage() => File.ReadAllBytes(ImagePath);
}
