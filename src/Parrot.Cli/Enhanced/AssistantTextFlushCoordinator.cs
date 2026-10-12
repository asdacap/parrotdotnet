using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AssistantTextFlushCoordinator(Func<string, CancellationToken, Task> flushChild)
{
    private Func<string, CancellationToken, Task>? _flushRoot;

    public void BindRoot(Func<string, CancellationToken, Task> flushRoot) => _flushRoot = flushRoot;

    public void UnbindRoot() => _flushRoot = null;

    public async Task Prepare(Event published, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);

        if (published.PayloadCase is not (
            Event.PayloadOneofCase.ReasoningChunk
            or Event.PayloadOneofCase.EncryptedReasoningReceived
            or Event.PayloadOneofCase.ToolCallChunk
            or Event.PayloadOneofCase.ToolRequestReceived
            or Event.PayloadOneofCase.ToolExecutionStarted
            or Event.PayloadOneofCase.ToolStarted
            or Event.PayloadOneofCase.ToolFinished
            or Event.PayloadOneofCase.ToolCancelled
            or Event.PayloadOneofCase.ToolError
            or Event.PayloadOneofCase.ToolSnapshot
            or Event.PayloadOneofCase.TurnStarted
            or Event.PayloadOneofCase.TurnEnded
            or Event.PayloadOneofCase.TurnFailed
            or Event.PayloadOneofCase.AgentStarted
            or Event.PayloadOneofCase.AgentFinished
            or Event.PayloadOneofCase.AgentFailed
            or Event.PayloadOneofCase.ModeTurnCompleted
            or Event.PayloadOneofCase.PlanCompleted))
        {
            return;
        }

        var owner = published.PayloadCase == Event.PayloadOneofCase.ToolSnapshot
            ? published.ToolSnapshot.OwnerAgentSessionId
            : published.AgentSessionId;
        if (_flushRoot is { } flushRoot)
        {
            await flushRoot(owner, cancellationToken).ConfigureAwait(false);
        }

        await flushChild(owner, cancellationToken).ConfigureAwait(false);
    }
}
