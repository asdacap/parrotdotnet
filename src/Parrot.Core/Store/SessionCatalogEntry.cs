namespace Parrot.Store;

internal sealed record SessionCatalogEntry
{
    public required UserSessionId Id { get; init; }

    public SessionCatalogState State { get; init; }

    public string WorkingDirectory { get; init; } = string.Empty;

    public string RootAgentName { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string? Selector { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string LastOpenedAt { get; init; } = string.Empty;

    public string CreatedAt { get; init; } = string.Empty;

    public static SessionCatalogEntry Corrupt(UserSessionId id) =>
        new() { Id = id, State = SessionCatalogState.Corrupt };
}
