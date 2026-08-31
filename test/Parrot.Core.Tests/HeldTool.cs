using Parrot.Agent;
using Parrot.Tools;

namespace Parrot.Core.Tests;

// A tool that never finishes on its own. It exists so a turn can be
// interrupted while its tools are still running, which is the only state where
// "every tool call settles" says anything.
internal sealed class HeldTool : ITool
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Name => "held";

    public Task Started => _started.Task;

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        _started.SetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

        return "unreachable";
    }
}
