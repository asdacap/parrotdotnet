namespace Parrot.Llm;

// A named request preset, such as a reasoning-effort level a model exposes.
internal sealed record ModelVariant(string Name, string ReasoningEffort);
