using Parrot.Llm;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed record AgentTurnSelection(
    ModelSelector RequestedModel,
    ResolvedModelSelection ResolvedModel,
    IMode Mode,
    SecurityProfile SecurityProfile)
{
    public IAgentProfile Profile => Mode.Profile;
}
