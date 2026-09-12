namespace Parrot.Config;

internal sealed record RequestLimitsConfig
{
    public int ImageBytesPerToolCycle { get; init; } = 16 << 20;

    public int ProviderRequestBytes { get; init; } = 64 << 20;
}
