using Parrot.Config;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class GoalService(
    IAgentSession session,
    PromptTemplateCatalog promptTemplates)
{
    public async Task SetGoal(string goal, CancellationToken cancellationToken)
    {
        var reminder = promptTemplates.Render(
            "goal.root-reminder",
            [new PromptTemplateArgument("goal", goal)]);
        session.SetExitReminder(reminder);
        var notice = promptTemplates.Render(
            "goal.root-reminder-notice",
            [new PromptTemplateArgument("reminder", reminder)]);
        _ = await session.Send(
            [ConversationPart.TextPart(notice)],
            Identifier.MessageId(),
            Delivery.Steer,
            cancellationToken).ConfigureAwait(false);
    }

    public void ClearGoal() => session.SetExitReminder(null);
}
