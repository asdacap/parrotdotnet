using Parrot.Llm;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed record AgentSelection(
    ModelSelector RequestedModel,
    MainAgentProfile? Profile,
    SecurityProfile SecurityProfile);
