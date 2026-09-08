namespace Parrot.Llm;

internal static class ImageGenerationLimits
{
    public const int MaximumReferences = 5;
    public const int MaximumReferenceBytes = 10 << 20;
    public const int MaximumReferenceBatchBytes = 50 << 20;
    public const int MaximumRequestBytes = 80 << 20;
    public const int MaximumResponseBytes = 64 << 20;
    public const int MaximumOutputBytes = 32 << 20;
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(10);
}
