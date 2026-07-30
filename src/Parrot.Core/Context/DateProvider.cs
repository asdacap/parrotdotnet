using Parrot.Agent;

namespace Parrot.Context;

internal sealed class DateProvider(string date) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:04-date";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt($"Date: {date}");
    }
}
