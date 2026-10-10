using Parrot.Store;

namespace Parrot.Protocol;

public sealed partial class ArtifactReference
{
    internal static ArtifactReference FromMetadata(ImageArtifactMetadata artifact) => new()
    {
        ArtifactId = artifact.ArtifactId,
        Sha256 = artifact.Sha256,
        MediaType = artifact.MediaType,
        ByteLength = artifact.ByteLength,
        Width = artifact.Width,
        Height = artifact.Height,
        FrameCount = artifact.FrameCount,
        AggregatePixels = artifact.AggregatePixels,
        DisplayName = artifact.DisplayName,
        Origin = artifact.Origin,
    };
}
