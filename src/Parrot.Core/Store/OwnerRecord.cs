namespace Parrot.Store;

internal sealed record OwnerRecord
{
    public required int Version { get; init; }

    public required string SessionId { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string HostKey { get; init; }

    public required int ProcessId { get; init; }
}
