namespace Parrot.Store;

internal sealed record RootAgentNameReservationToken(
    string RootAgentName,
    string SessionId,
    int Version,
    bool Acquired);
