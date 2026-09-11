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
    string Origin)
{
    public bool MatchesContent(ImageArtifactMetadata artifact) =>
        ArtifactId == artifact.ArtifactId
        && Sha256 == artifact.Sha256
        && MediaType == artifact.MediaType
        && ByteLength == artifact.ByteLength
        && Width == artifact.Width
        && Height == artifact.Height
        && FrameCount == artifact.FrameCount
        && AggregatePixels == artifact.AggregatePixels;
}
