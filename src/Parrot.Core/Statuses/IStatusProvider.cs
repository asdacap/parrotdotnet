namespace Parrot.Statuses;

internal interface IStatusProvider
{
    string Key { get; }

    ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken);
}
