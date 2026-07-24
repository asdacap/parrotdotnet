namespace Parrot.Statuses;

internal interface IActiveWorkSource
{
    IReadOnlyList<ActiveWorkObservation> Active();
}
