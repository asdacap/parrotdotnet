using Parrot.Llm;

namespace Parrot.Agent;

internal sealed record AgentSelection(ILLMProvider Provider, string Model, ModeProfile? Mode);
