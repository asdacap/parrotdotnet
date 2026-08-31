using Parrot.Protocol;

namespace Parrot.Agent;

internal sealed record ModeCompletionOutcome(PlanCompleted? Completion, string? RepairDiagnostic)
{
    internal static ModeCompletionOutcome None { get; } = new(null, null);

    internal static ModeCompletionOutcome Completed(PlanCompleted completion) =>
        new(completion ?? throw new ArgumentNullException(nameof(completion)), null);

    internal static ModeCompletionOutcome Repair(string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            throw new ArgumentException("A repair diagnostic is required.", nameof(diagnostic));
        }

        return new ModeCompletionOutcome(null, diagnostic);
    }
}
