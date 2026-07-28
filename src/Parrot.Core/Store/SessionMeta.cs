using Parrot.Agent;

namespace Parrot.Store;

// The published projection of a session. The database stays the source of
// truth; this exists so listing never opens it.
internal sealed record SessionMeta
{
    public required string Id { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string HostKey { get; init; }

    public string Name { get; init; } = string.Empty;

    public required string ProviderId { get; init; }

    public required string Model { get; init; }

    public string Mode { get; init; } = ModeRegistry.Build;

    public int ProcessId { get; init; }

    public string CreatedAt { get; init; } = string.Empty;
}
