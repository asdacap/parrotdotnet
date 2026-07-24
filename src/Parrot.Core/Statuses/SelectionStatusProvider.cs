namespace Parrot.Statuses;

internal sealed class SelectionStatusProvider : IStatusProvider
{
    public string Key => "runtime:selection";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var variant = string.IsNullOrWhiteSpace(query.Variant) ? string.Empty : $"\nVariant: {query.Variant}";
        return ValueTask.FromResult(StatusObservation.AvailableText(
            $"Active profile: {query.Profile}\nModel: {query.Provider}/{query.Model}{variant}"));
    }
}
