using Parrot.Agent;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class FailureTool : ITool
{
    public string Name => "failure";

    public Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("tool failed");
}
