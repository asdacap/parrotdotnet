using System.Security.Cryptography;
using Parrot.Files;

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
        PrivateFile.EnsureDirectory(_directory);
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

    public Stream Open(string artifactId) =>
        new FileStream(PathOf(artifactId), FileMode.Open, FileAccess.Read, FileShare.Read);

    public string PathOf(string artifactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        if (artifactId.Length != 64 || !artifactId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("An artifact id must be a SHA-256 hexadecimal value.", nameof(artifactId));
        }

        var paths = Directory.EnumerateFiles(_directory, $"{artifactId}.*");
        return paths.SingleOrDefault()
            ?? throw new FileNotFoundException("The image artifact does not exist.", artifactId);
    }

    private static async Task<long> Copy(Stream source, string staging, CancellationToken cancellationToken)
    {
        long length = 0;
        await using var destination = PrivateFile.CreateNew(staging, FileShare.Read, FileOptions.Asynchronous);
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
        var bytes = await File.ReadAllBytesAsync(staging, cancellationToken).ConfigureAwait(false);
        var image = ImageInspection.Inspect(bytes);
        var framePixels = checked((long)image.Width * image.Height);
        var aggregatePixels = checked(framePixels * image.FrameCount);
        if (image.Width > ImageArtifactLimits.MaximumDimension
            || image.Height > ImageArtifactLimits.MaximumDimension
            || framePixels > ImageArtifactLimits.MaximumFramePixels
            || image.FrameCount > ImageArtifactLimits.MaximumFrames
            || aggregatePixels > ImageArtifactLimits.MaximumAggregatePixels)
        {
            throw new InvalidDataException("The image dimensions or frame count exceed the supported limits.");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new ImageArtifactMetadata(hash, hash, image.MediaType, byteLength, image.Width, image.Height, image.FrameCount, aggregatePixels, displayName, origin);
    }

    private static void ValidateLabel(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("An image label cannot contain control characters.", parameterName);
        }
    }

    private static string Extension(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => throw new InvalidDataException("The image media type is not supported."),
    };
}
