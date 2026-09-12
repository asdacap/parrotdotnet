namespace Parrot.Store;

internal sealed record OwnerRecord
{
    public int? SchemaVersion { get; init; }

    public int? Version { get; init; }

    public long? Generation { get; init; }

    public string? State { get; init; }

    public string? UserSessionId { get; init; }

    public string? SessionId { get; init; }

    public string? CanonicalWorkspaceIdentity { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? HostIdentity { get; init; }

    public string? HostKey { get; init; }

    public string? BootIdentity { get; init; }

    public int? ProcessId { get; init; }

    public string? ProcessStartToken { get; init; }

    public string? RuntimeInstanceId { get; init; }

    public string? LeaseId { get; init; }
}
