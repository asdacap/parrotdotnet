using Parrot.Agent;
using Parrot.Tools;

namespace Parrot.Core.Tests;

// A tool call that finishes, so a turn can reach its next boundary without
// being stopped to get there. HeldTool is its opposite and they are the two
// halves of what a tool round can do to a drain.
internal sealed class SettledTool(string result) : ITool
{
    public string Name => "settled";

    public bool IsParallelSafe(ToolInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return true;
    }

    public Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken) =>
        Task.FromResult<ToolExecutionResult>(result);
}
