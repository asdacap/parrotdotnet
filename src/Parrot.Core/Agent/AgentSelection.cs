using Parrot.Llm;

namespace Parrot.Agent;

internal sealed record AgentSelection(ProviderModel ResolvedModel, ModeProfile? Mode);
