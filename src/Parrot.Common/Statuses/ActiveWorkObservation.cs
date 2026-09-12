namespace Parrot.Statuses;

internal sealed record ActiveWorkObservation(
    string Id,
    string Name,
    ActiveWorkKind Kind,
    ActiveWorkState State);
