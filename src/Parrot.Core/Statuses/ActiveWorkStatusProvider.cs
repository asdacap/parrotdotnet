namespace Parrot.Statuses;

internal sealed class ActiveWorkStatusProvider(params IActiveWorkSource[] sources) : IStatusProvider
{
    public string Key => "runtime:tasks";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = sources
            .SelectMany(source => source.Active())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        if (active.Length == 0)
        {
            return ValueTask.FromResult(StatusObservation.AvailableText("Active tasks: none"));
        }

        var lines = active.Select(item =>
            $"- {item.Id} ({Kind(item.Kind)}, {State(item.State)}, name: {item.Name})");
        return ValueTask.FromResult(StatusObservation.AvailableText($"Active tasks:\n{string.Join("\n", lines)}"));
    }

    private static string Kind(ActiveWorkKind kind) => kind switch
    {
        ActiveWorkKind.Agent => "agent",
        ActiveWorkKind.Shell => "shell",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown active work kind."),
    };

    private static string State(ActiveWorkState state) => state switch
    {
        ActiveWorkState.Running => "running",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown active work state."),
    };
}
