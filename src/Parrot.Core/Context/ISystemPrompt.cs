using Parrot.Agent;

namespace Parrot.Context;

internal interface ISystemPrompt
{
    void RenewEpoch();

    string Build(AgentTurnSelection selection);
}
