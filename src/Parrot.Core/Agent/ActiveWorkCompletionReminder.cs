using Parrot.Config;

namespace Parrot.Agent;

internal sealed class ActiveWorkCompletionReminder(
    IReadOnlyList<IActiveWorkBlocker> blockers,
    IPromptTemplateCatalog promptTemplates)
{
    public string? Build()
    {
        var workSections = new List<string>();
        var reminders = new List<string>();
        var hasBlockingWork = false;
        foreach (var blocker in blockers)
        {
            var result = blocker.Observe();
            if (result is null)
            {
                continue;
            }

            hasBlockingWork = true;
            if (result.WorkSection is not null)
            {
                workSections.Add(result.WorkSection);
            }

            if (result.Reminder is not null)
            {
                reminders.Add(result.Reminder);
            }
        }

        if (!hasBlockingWork)
        {
            return null;
        }

        if (workSections.Count > 0)
        {
            reminders.Insert(
                0,
                promptTemplates.Render(
                    "agent-session.active-work-reminder",
                    [new PromptTemplateArgument("active_work", string.Concat(workSections))]));
        }

        return string.Join('\n', reminders);
    }
}
