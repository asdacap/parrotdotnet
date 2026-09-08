using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace Parrot.Llm;

internal static class ImageGenerationImage
{
    public static async Task<string> Validate(byte[] data, bool requirePng, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var format = await Image.DetectFormatAsync(stream, cancellationToken).ConfigureAwait(false);
            var mediaType = format.DefaultMimeType switch
            {
                "image/png" => "image/png",
                "image/jpeg" when !requirePng => "image/jpeg",
                "image/webp" when !requirePng => "image/webp",
                _ => throw new LLMProviderException("provider: unsupported image format"),
            };
            stream.Position = 0;
            var info = await Image.IdentifyAsync(stream, cancellationToken).ConfigureAwait(false);
            if (info.Width > 8192 || info.Height > 8192 || (long)info.Width * info.Height > 40_000_000
                || info.FrameMetadataCollection.Count > 1)
            {
                throw new LLMProviderException("provider: image dimensions or frames exceed supported limits");
            }

            stream.Position = 0;
            using var image = await Image.LoadAsync(new DecoderOptions { MaxFrames = 2 }, stream, cancellationToken).ConfigureAwait(false);
            if (image.Frames.Count != 1)
            {
                throw new LLMProviderException("provider: animated images are not supported");
            }

            return mediaType;
        }
        catch (Exception failure) when (failure is UnknownImageFormatException or InvalidImageContentException)
        {
            throw new LLMProviderException("provider: invalid image content");
        }
    }
}
