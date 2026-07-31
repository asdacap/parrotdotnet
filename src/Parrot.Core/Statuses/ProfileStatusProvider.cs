namespace Parrot.Statuses;

internal sealed class ProfileStatusProvider(string key, string prompt) : IStatusProvider
{
    public string Key { get; } = key;

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            string.IsNullOrWhiteSpace(prompt)
                ? StatusObservation.Unavailable
                : StatusObservation.AvailableText(prompt));
    }
}
