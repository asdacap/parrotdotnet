namespace Parrot.Llm;

internal sealed record BuiltProvider(ILLMProvider Provider, IReadOnlyList<LLMModel> Seed)
{
    public static BuiltProvider CreateWithSeed(ILLMProvider provider) => new(provider, provider.SeedModels());
}
