using Parrot.Llm;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed record AgentTurnSelection(
    ModelSelector RequestedModel,
    ResolvedModelSelection ResolvedModel,
    IMode Profile,
    SecurityProfile SecurityProfile);
