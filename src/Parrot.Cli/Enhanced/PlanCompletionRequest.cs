using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class PlanCompletionRequest(PlanCompleted completed)
{
    public PlanCompleted Completed { get; } = completed;

    public TaskCompletionSource Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
