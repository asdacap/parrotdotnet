using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Context;

internal sealed class QueueGuidancePrompt(PromptTemplateCatalog templates) : ISystemPrompt
{
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

        var guidanceId = (allowedTools is null || (allowedTools.Contains("queue_push", StringComparer.Ordinal)
                && allowedTools.Contains("queue_take", StringComparer.Ordinal)))
            && !disabledTools.Contains("queue_push", StringComparer.Ordinal)
            && !disabledTools.Contains("queue_take", StringComparer.Ordinal)
            ? "context.queue-guidance-with-close"
            : "context.queue-guidance";
        return templates.Render(guidanceId, []);
    }
}
