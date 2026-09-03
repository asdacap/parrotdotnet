using Parrot.Config;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class ExitReminder(
    EventRepository repository,
    PromptTemplateCatalog promptTemplates,
    string agentSessionId)
{
    private readonly Lock _gate = new();
    private string? _current = repository.LatestExitReminder(agentSessionId);

    public void Set(string? reminder)
    {
        var normalized = reminder is { Length: > 0 } ? reminder : null;
        lock (_gate)
        {
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
            return _current is null
                ? null
                : promptTemplates.Render(
                    "agent-session.exit-reminder",
                    [new PromptTemplateArgument("reminder", _current)]);
        }
    }
}
