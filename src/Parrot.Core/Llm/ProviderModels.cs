using Parrot.Config;

namespace Parrot.Llm;

internal static class ProviderModels
{
    public static IReadOnlyList<LLMModel> ReadDeclared(
        string providerId,
        IReadOnlyDictionary<string, ModelConfig> models) => Read(providerId, models, sortVariants: true);

    public static IReadOnlyList<LLMModel> ReadDefaults(
        string providerId,
        IReadOnlyDictionary<string, ModelConfig> models) => Read(providerId, models, sortVariants: false);

    private static IReadOnlyList<LLMModel> Read(
        string providerId,
        IReadOnlyDictionary<string, ModelConfig> models,
        bool sortVariants) =>
        [
            .. models.Select(entry => new LLMModel(entry.Key, providerId)
            {
                Name = entry.Value.Name,
                ContextWindow = entry.Value.Context,
                MaxOutputTokens = entry.Value.MaxTokens,
                Capabilities = new ModelCapabilities(
                    entry.Value.Tools,
                    entry.Value.Reasoning || entry.Value.Variants.Count > 0,
                    entry.Value.Output.Count > 0 ? entry.Value.Output : ["text"],
                    [.. (sortVariants
                        ? entry.Value.Variants.OrderBy(variant => variant.Key, StringComparer.Ordinal)
                        : entry.Value.Variants.AsEnumerable()).Select(variant => new ModelVariant(variant.Key, variant.Value))]),
            }),
        ];
}
