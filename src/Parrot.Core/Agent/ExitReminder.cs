using Parrot.Config;
using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class ExitReminder(
    IEventRepository repository,
    IEventBroker eventBroker,
    IPromptTemplateCatalog promptTemplates,
    string agentSessionId) : IExitReminder
{
    private readonly Lock _gate = new();
    private readonly List<ExitReminderEntry> _current = [.. repository.ExitReminders(agentSessionId)];
    private int _count;

    public IReadOnlyList<string> Titles
    {
        get
        {
            lock (_gate)
            {
                return [.. _current.Select(reminder => reminder.Title)];
            }
        }
    }

    public async Task Set(string title, string description, CancellationToken cancellationToken)
    {
        Event published;
        lock (_gate)
        {
            var index = _current.FindIndex(reminder => reminder.Title == title);
            if (index < 0 || _current[index].Description != description)
            {
                _count = 0;
            }

            published = Append(title, description);
            var entry = new ExitReminderEntry(title, description);
            if (index < 0)
            {
                _current.Add(entry);
            }
            else
            {
                _current[index] = entry;
            }
        }

        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> Clear(string title, CancellationToken cancellationToken)
    {
        Event published;
        lock (_gate)
        {
            if (_current.RemoveAll(reminder => reminder.Title == title) == 0)
            {
                return false;
            }

            _count = 0;
            published = Append(title, null);
        }

        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public string? Build()
    {
        lock (_gate)
        {
            if (_current.Count == 0)
            {
                return null;
            }

            var reminders = string.Join('\n', _current.Select(reminder => $"- {reminder.Title}: {reminder.Description}"));
            if (_count == 0)
            {
                _count = 1;
                return promptTemplates.Render(
                    "agent-session.exit-reminder",
                    [new PromptTemplateArgument("reminders", reminders)]);
            }

            _count++;
            return promptTemplates.Render(
                "agent-session.exit-reminder-nth",
                [
                    new PromptTemplateArgument("reminders", reminders),
                    new PromptTemplateArgument("ordinal", FormatOrdinal(_count)),
                ]);
        }
    }

    private static string FormatOrdinal(int number)
    {
        var tens = number % 10;
        if (number % 100 is 11 or 12 or 13)
        {
            tens = 0;
        }

        var suffix = tens switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
        return $"{number}{suffix}";
    }

    private Event Append(string title, string? description)
    {
        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = agentSessionId,
        };
        repository.AppendExitReminderChanged(published, title, description);
        return published;
    }
}
