using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;

namespace Parrot.Queues;

internal sealed class QueueGuidancePrompt(IPromptTemplateCatalog templates) : ISystemPrompt
{
    public void RenewEpoch()
    {
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var profile = selection.Profile;
        if (!profile.IsToolPermitted("queue_create"))
        {
            return string.Empty;
        }

        var guidanceId = profile.IsToolPermitted("queue_push") && profile.IsToolPermitted("queue_take")
            ? "context.queue-guidance-with-close"
            : "context.queue-guidance";
        return templates.Render(guidanceId, []);
    }
}
