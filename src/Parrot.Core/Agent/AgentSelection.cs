using Parrot.Llm;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed record AgentSelection(
    ProviderModel ResolvedModel,
    MainAgentProfile? Profile,
    SecurityProfile SecurityProfile);
