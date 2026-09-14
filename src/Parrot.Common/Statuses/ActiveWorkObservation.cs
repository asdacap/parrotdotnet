namespace Parrot.Statuses;

internal sealed record ActiveWorkObservation(
    string Id,
    string Name,
    ActiveWorkState State);
