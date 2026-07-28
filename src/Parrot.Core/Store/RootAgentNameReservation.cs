namespace Parrot.Store;

internal sealed record RootAgentNameReservation
{
    public required int Version { get; init; }

    public string SessionId { get; init; } = string.Empty;

    public string HostKey { get; init; } = string.Empty;

    public int ProcessId { get; init; }
}
