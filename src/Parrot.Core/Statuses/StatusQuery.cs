namespace Parrot.Statuses;

internal sealed record StatusQuery(
    string SessionId,
    string Profile,
    string Provider,
    string Model,
    string Variant);
