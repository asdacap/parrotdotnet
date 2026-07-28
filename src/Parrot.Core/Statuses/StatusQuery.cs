namespace Parrot.Statuses;

internal sealed record StatusQuery(
    string SessionId,
    string ParentSessionId,
    string ParentSessionName,
    string Profile,
    string Provider,
    string Model,
    string Variant);
