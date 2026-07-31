namespace Parrot.Store;

internal sealed record ImageArtifactMetadata(
    string ArtifactId,
    string Sha256,
    string MediaType,
    long ByteLength,
    int Width,
    int Height,
    int FrameCount,
    long AggregatePixels,
    string DisplayName,
    string Origin);
