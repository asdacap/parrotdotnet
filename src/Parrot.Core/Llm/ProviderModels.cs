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
                MaxInputTokens = entry.Value.MaxInputTokens,
                InputPrice = entry.Value.InputPrice,
                CachedInputPrice = entry.Value.CachedInputPrice,
                OutputPrice = entry.Value.OutputPrice,
                Capabilities = new ModelCapabilities(
                    entry.Value.Tools,
                    entry.Value.Reasoning || entry.Value.Variants.Count > 0,
                    entry.Value.Output.Count > 0 ? entry.Value.Output : ["text"],
                    [.. (sortVariants
                        ? entry.Value.Variants.OrderBy(variant => variant.Key, StringComparer.Ordinal)
                        : entry.Value.Variants.AsEnumerable()).Select(variant => new ModelVariant(variant.Key, variant.Value))]),
                Fields = ConvertFields(entry.Value.Fields),
            }),
        ];

    private static ModelMetadataFields ConvertFields(ModelConfigFields fields)
    {
        var result = ModelMetadataFields.None;
        result |= fields.HasFlag(ModelConfigFields.Name) ? ModelMetadataFields.Name : ModelMetadataFields.None;
        result |= fields.HasFlag(ModelConfigFields.Context) ? ModelMetadataFields.ContextWindow : ModelMetadataFields.None;
        result |= fields.HasFlag(ModelConfigFields.MaxTokens) ? ModelMetadataFields.MaxOutputTokens : ModelMetadataFields.None;
        result |= fields.HasFlag(ModelConfigFields.MaxInputTokens) ? ModelMetadataFields.MaxInputTokens : ModelMetadataFields.None;
        result |= fields.HasFlag(ModelConfigFields.InputPrice) ? ModelMetadataFields.InputPrice : ModelMetadataFields.None;
        result |= fields.HasFlag(ModelConfigFields.CachedInputPrice) ? ModelMetadataFields.CachedInputPrice : ModelMetadataFields.None;
        result |= fields.HasFlag(ModelConfigFields.OutputPrice) ? ModelMetadataFields.OutputPrice : ModelMetadataFields.None;
        result |= fields.HasFlag(ModelConfigFields.Tools) ? ModelMetadataFields.Tools : ModelMetadataFields.None;
        result |= fields.HasFlag(ModelConfigFields.Reasoning) ? ModelMetadataFields.Reasoning : ModelMetadataFields.None;
        result |= fields.HasFlag(ModelConfigFields.Output) ? ModelMetadataFields.Output : ModelMetadataFields.None;
        result |= fields.HasFlag(ModelConfigFields.Variants) ? ModelMetadataFields.Variants : ModelMetadataFields.None;
        return result;
    }
}
