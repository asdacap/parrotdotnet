using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace Parrot.Store;

internal sealed class ImageArtifactStore
{
    private readonly string _directory;

    public ImageArtifactStore(UserSessionResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _directory = resources.ArtifactDirectory;
    }

    public Task<ImageArtifactMetadata> Persist(
        Stream source,
        string displayName,
        string origin,
        CancellationToken cancellationToken) =>
        Persist(source, displayName, origin, string.Empty, -1, cancellationToken);

    public async Task<ImageArtifactMetadata> Persist(
        Stream source,
        string displayName,
        string origin,
        string declaredMediaType,
        long declaredByteLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateLabel(displayName, nameof(displayName));
        ValidateLabel(origin, nameof(origin));
        EnsureDirectory(_directory);
        var staging = Path.Combine(_directory, $".staging-{Guid.NewGuid():n}");
        try
        {
            var byteLength = await Copy(source, staging, cancellationToken).ConfigureAwait(false);
            var metadata = await Validate(staging, byteLength, displayName, origin, cancellationToken).ConfigureAwait(false);
            if (declaredByteLength >= 0 && metadata.ByteLength != declaredByteLength)
            {
                throw new InvalidDataException("The image byte length does not match its declaration.");
            }

            if (declaredMediaType.Length > 0
                && !string.Equals(metadata.MediaType, declaredMediaType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The image media type does not match its declaration.");
            }

            var destination = Path.Combine(_directory, $"{metadata.ArtifactId}{Extension(metadata.MediaType)}");
            try
            {
                File.Move(staging, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                File.Delete(staging);
            }

            return metadata;
        }
        catch (Exception failure) when (failure is UnknownImageFormatException or InvalidImageContentException)
        {
            File.Delete(staging);
            throw new InvalidDataException("The image content is invalid or unsupported.", failure);
        }
        catch
        {
            File.Delete(staging);
            throw;
        }
    }

    public void Delete(ImageArtifactMetadata artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var path = Path.Combine(_directory, $"{artifact.ArtifactId}{Extension(artifact.MediaType)}");
        File.Delete(path);
    }

    public Stream Open(string artifactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        if (artifactId.Length != 64 || !artifactId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("An artifact id must be a SHA-256 hexadecimal value.", nameof(artifactId));
        }

        var paths = Directory.EnumerateFiles(_directory, $"{artifactId}.*");
        var path = paths.SingleOrDefault()
            ?? throw new FileNotFoundException("The image artifact does not exist.", artifactId);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static async Task<long> Copy(Stream source, string staging, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        long length = 0;
        await using var destination = new FileStream(staging, options);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (length > ImageArtifactLimits.MaximumImageBytes - read)
            {
                throw new InvalidDataException("An image cannot exceed 5 MiB.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            length += read;
        }

        return length;
    }

    private static async Task<ImageArtifactMetadata> Validate(
        string staging,
        long byteLength,
        string displayName,
        string origin,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(staging);
        var format = await Image.DetectFormatAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The image format is not supported.");
        var mediaType = MediaType(format);
        stream.Position = 0;
        var info = await Image.IdentifyAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The image content is invalid or unsupported.");
        var frameCount = Math.Max(1, info.FrameMetadataCollection.Count);
        var framePixels = checked((long)info.Width * info.Height);
        var aggregatePixels = checked(framePixels * frameCount);
        if (info.Width > ImageArtifactLimits.MaximumDimension
            || info.Height > ImageArtifactLimits.MaximumDimension
            || framePixels > ImageArtifactLimits.MaximumFramePixels
            || frameCount > ImageArtifactLimits.MaximumFrames
            || aggregatePixels > ImageArtifactLimits.MaximumAggregatePixels)
        {
            throw new InvalidDataException("The image dimensions or frame count exceed the supported limits.");
        }

        stream.Position = 0;
        using var image = await Image.LoadAsync(
            new DecoderOptions { MaxFrames = ImageArtifactLimits.MaximumFrames + 1 },
            stream,
            cancellationToken).ConfigureAwait(false);
        if (image.Frames.Count != frameCount || image.Width != info.Width || image.Height != info.Height)
        {
            throw new InvalidDataException("The decoded image metadata does not match its inspected metadata.");
        }

        stream.Position = 0;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return new ImageArtifactMetadata(hash, hash, mediaType, byteLength, info.Width, info.Height, frameCount, aggregatePixels, displayName, origin);
    }

    private static void ValidateLabel(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("An image label cannot contain control characters.", parameterName);
        }
    }

    private static string MediaType(IImageFormat format) => format.Name switch
    {
        "PNG" => "image/png",
        "JPEG" => "image/jpeg",
        "GIF" => "image/gif",
        "WEBP" => "image/webp",
        _ => throw new InvalidDataException("Only PNG, JPEG, GIF, and WebP images are supported."),
    };

    private static string Extension(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => throw new InvalidDataException("The image media type is not supported."),
    };

    private static void EnsureDirectory(string directory)
    {
        _ = Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
