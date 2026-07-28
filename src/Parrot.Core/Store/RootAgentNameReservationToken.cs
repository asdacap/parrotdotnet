namespace Parrot.Store;

internal sealed record RootAgentNameReservationToken(
    string Name,
    string SessionId,
    int Version,
    bool Acquired);
