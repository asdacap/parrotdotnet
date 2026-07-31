namespace Parrot.Statuses;

internal sealed class SelectionStatusProvider : IStatusProvider
{
    public string Key => "runtime:selection";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = string.IsNullOrWhiteSpace(query.ParentSessionId)
            ? string.Empty
            : string.IsNullOrWhiteSpace(query.ParentSessionName)
                ? $"\nParent session: {query.ParentSessionId}"
                : $"\nParent session: {query.ParentSessionId} ({query.ParentSessionName})";
        return ValueTask.FromResult(StatusObservation.AvailableText(
            $"Active profile: {query.Profile}\nModel: {query.RequestedModel}{parent}"));
    }
}
