namespace Parrot.Statuses;

/// <summary>Contributes one independently observed status section for the session and selection in a query.</summary>
internal interface IStatusProvider
{
    /// <summary>Gets the stable namespaced key used for registration and section ordering.</summary>
    string Key { get; }

    /// <summary>Observes the supplied query, returning an unavailable observation when no section applies.</summary>
    ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken);
}
