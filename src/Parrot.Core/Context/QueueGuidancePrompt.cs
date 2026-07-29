using Parrot.Agent;

namespace Parrot.Context;

internal sealed class QueueGuidancePrompt : ISystemPrompt
{
    private const string Guidance =
        "When a task have many work item, use queue and multiple worker subagent. Publisher and consumer can be spawned at the same time.";

    public void RenewEpoch()
    {
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var allowedTools = selection.Profile?.AllowedTools;
        return allowedTools is null || allowedTools.Contains("queue_create", StringComparer.Ordinal)
            ? Guidance
            : string.Empty;
    }
}
