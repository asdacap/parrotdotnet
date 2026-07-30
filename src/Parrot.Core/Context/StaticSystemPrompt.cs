using Parrot.Agent;

namespace Parrot.Context;

internal sealed class StaticSystemPrompt(string content) : ISystemPrompt
{
    public void RenewEpoch()
    {
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return content;
    }
}
