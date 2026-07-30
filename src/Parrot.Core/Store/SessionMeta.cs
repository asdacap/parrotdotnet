namespace Parrot.Store;

// The published projection of a session. The database stays the source of
// truth; this exists so listing never opens it.
internal sealed record SessionMeta
{
    public required string Id { get; init; }

    public required string WorkingDirectory { get; init; }

    public string RootAgentName { get; init; } = string.Empty;

    public required string ProviderId { get; init; }

    public required string Model { get; init; }

    public string Selector { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string CreatedAt { get; init; } = string.Empty;
}
