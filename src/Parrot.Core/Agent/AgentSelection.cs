using Parrot.Llm;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed record AgentSelection(
    ModelSelector RequestedModel,
    IMode Mode,
    SecurityProfile SecurityProfile)
{
    public IAgentProfile Profile => Mode.Profile;
}
