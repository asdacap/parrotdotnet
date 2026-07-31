namespace Parrot.Statuses;

internal sealed class ActiveWorkStatusProvider(
    ActiveWorkKind workKind,
    IActiveWorkSource source) : IStatusProvider
{
    public string Key => workKind switch
    {
        ActiveWorkKind.Agent => "runtime:tasks-subagents",
        ActiveWorkKind.Shell => "runtime:tasks-processes",
        _ => throw new ArgumentOutOfRangeException(nameof(workKind), workKind, "Unknown active work kind."),
    };

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = source.Active()
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var heading = Heading(workKind);

        if (active.Length == 0)
        {
            return ValueTask.FromResult(StatusObservation.AvailableText($"{heading}: none"));
        }

        var lines = active.Select(item =>
            $"- {item.Id} ({Kind(item.Kind)}, {State(item.State)}, name: {item.Name})");
        return ValueTask.FromResult(StatusObservation.AvailableText($"{heading}:\n{string.Join("\n", lines)}"));
    }

    private static string Heading(ActiveWorkKind kind) => kind switch
    {
        ActiveWorkKind.Agent => "Active subagents",
        ActiveWorkKind.Shell => "Active processes",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown active work kind."),
    };

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
