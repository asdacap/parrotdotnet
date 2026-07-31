namespace Parrot.Llm;

internal sealed record BuiltProvider(ILLMProvider Provider, IReadOnlyList<LLMModel> Seed);
