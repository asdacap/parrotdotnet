using Parrot.Store;

namespace Parrot.Llm;

internal static class ImageGenerationImage
{
    public static string Validate(byte[] data, bool requirePng)
    {
        var mediaType = ImageInspection.DetectMediaType(data) switch
        {
            "image/png" => "image/png",
            "image/jpeg" when !requirePng => "image/jpeg",
            "image/webp" when !requirePng => "image/webp",
            _ => throw new LLMProviderException("provider: unsupported image format"),
        };

        ImageInspection image;
        try
        {
            image = ImageInspection.Inspect(data);
        }
        catch (InvalidDataException)
        {
            throw new LLMProviderException("provider: invalid image content");
        }

        if (image.Width > 8192 || image.Height > 8192 || (long)image.Width * image.Height > 40_000_000
            || image.FrameCount > 1)
        {
            throw new LLMProviderException("provider: image dimensions or frames exceed supported limits");
        }

        return mediaType;
    }
}
