using Parrot.Llm;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed record AgentSelection(
    ProviderModel ResolvedModel,
    ModeProfile? Mode,
    SecurityProfile SecurityProfile);
