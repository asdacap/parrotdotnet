using Parrot.Agent;

namespace Parrot.Context;

internal sealed class QueueGuidancePrompt : ISystemPrompt
{
    private const string Guidance =
        "When a task has many work items, use a parent-owned queue and multiple worker subagents. "
        + "Each agent resolves its own queues first and can also access only its direct parent's queues; queue names must be unique across that edge in either creation order, while siblings may reuse a name. "
        + "Queue listening is configured independently for each invoking agent. Root queues persist with the user session, but child-owned queues last only for that child's lifetime.";

    private const string CloseGuidance =
        " Producers close a queue with queue_push(close:true) when no more items will arrive; items may be empty when closing. Consumers drain until queue_take reports closed with no items rather than guessing from an empty timeout.";

    public void RenewEpoch()
    {
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var allowedTools = selection.Profile.AllowedTools;
        var disabledTools = selection.Profile.DisabledTools;
        if ((allowedTools is not null && !allowedTools.Contains("queue_create", StringComparer.Ordinal))
            || disabledTools.Contains("queue_create", StringComparer.Ordinal))
        {
            return string.Empty;
        }

        return (allowedTools is null || (allowedTools.Contains("queue_push", StringComparer.Ordinal)
                && allowedTools.Contains("queue_take", StringComparer.Ordinal)))
            && !disabledTools.Contains("queue_push", StringComparer.Ordinal)
            && !disabledTools.Contains("queue_take", StringComparer.Ordinal)
            ? Guidance + CloseGuidance
            : Guidance;
    }
}
