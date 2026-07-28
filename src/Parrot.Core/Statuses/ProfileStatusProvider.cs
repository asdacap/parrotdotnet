namespace Parrot.Statuses;

internal sealed class ProfileStatusProvider(
    string key,
    string prompt,
    IReadOnlyList<string> hardRules,
    string status) : IStatusProvider
{
    public string Key { get; } = key;

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sections = new List<string>(2);
        var rules = hardRules.Where(rule => !string.IsNullOrWhiteSpace(rule)).ToArray();
        var instructions = prompt;

        if (rules.Length > 0)
        {
            instructions += $"\n\nHard rules:\n- {string.Join("\n- ", rules)}";
        }

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            sections.Add(instructions);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            sections.Add(status);
        }

        var text = string.Join("\n\n", sections);
        return ValueTask.FromResult(new StatusObservation(!string.IsNullOrWhiteSpace(text), text));
    }
}
