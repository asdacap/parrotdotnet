using Parrot.Config;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class ExitReminder(
    IEventRepository repository,
    IPromptTemplateCatalog promptTemplates,
    string agentSessionId)
{
    private readonly Lock _gate = new();
    private string? _current = repository.LatestExitReminder(agentSessionId);
    private int _count;

    public void Set(string? reminder)
    {
        var normalized = reminder is { Length: > 0 } ? reminder : null;
        lock (_gate)
        {
            if (normalized != _current)
            {
                _count = 0;
            }

            var published = new Parrot.Protocol.Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = agentSessionId,
            };
            repository.AppendExitReminderChanged(published, normalized);
            _current = normalized;
        }
    }

    public string? Build()
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return null;
            }

            if (_count == 0)
            {
                _count = 1;
                return promptTemplates.Render(
                    "agent-session.exit-reminder",
                    [new PromptTemplateArgument("reminder", _current)]);
            }

            _count++;
            return promptTemplates.Render(
                "agent-session.exit-reminder-nth",
                [
                    new PromptTemplateArgument("reminder", _current),
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
}
