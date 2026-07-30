namespace Parrot.Statuses;

internal sealed class ProfileStatusProvider(
    string key,
    string prompt,
    string status) : IStatusProvider
{
    public string Key { get; } = key;

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sections = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            sections.Add(prompt);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            sections.Add(status);
        }

        var text = string.Join("\n\n", sections);
        return ValueTask.FromResult(new StatusObservation(!string.IsNullOrWhiteSpace(text), text));
    }
}
