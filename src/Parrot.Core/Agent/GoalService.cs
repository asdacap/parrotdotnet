using Parrot.Config;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class GoalService(
    IAgentSession session,
    IExitReminder exitReminder,
    IPromptTemplateCatalog promptTemplates) : IGoalService
{
    private const string GoalTitle = "goal";

    public async Task SetGoal(string goal, CancellationToken cancellationToken)
    {
        var reminder = promptTemplates.Render(
            "goal.root-reminder",
            [new PromptTemplateArgument("goal", goal)]);
        await exitReminder.Set(GoalTitle, reminder, cancellationToken).ConfigureAwait(false);
        var notice = promptTemplates.Render(
            "goal.root-reminder-notice",
            [new PromptTemplateArgument("reminder", reminder)]);
        _ = await session.Send(
            [ConversationPart.TextPart(notice)],
            Identifier.MessageId(),
            Delivery.Steer,
            new IncomingActivity(string.Empty, null),
            cancellationToken).ConfigureAwait(false);
    }

    public Task ClearGoal(CancellationToken cancellationToken) =>
        exitReminder.Clear(GoalTitle, cancellationToken);
}
