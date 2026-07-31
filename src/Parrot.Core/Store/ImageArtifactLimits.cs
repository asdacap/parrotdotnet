namespace Parrot.Store;

internal static class ImageArtifactLimits
{
    public const int MaximumImages = 16;
    public const long MaximumImageBytes = 5 * 1024 * 1024;
    public const long MaximumBatchBytes = 20 * 1024 * 1024;
    public const int MaximumDimension = 8192;
    public const long MaximumFramePixels = 40_000_000;
    public const int MaximumFrames = 100;
    public const long MaximumAggregatePixels = 100_000_000;
}
