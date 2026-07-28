using Parrot.Context;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal static class TestModels
{
    public static ISystemPromptProvider PromptProvider(string workingDirectory, string configDirectory) =>
        new CompositeSystemPromptProvider(
            "test:system-prompt",
            [
                new SystemContextProvider(workingDirectory, configDirectory, "2026-07-24"),
                new ModelPromptProvider(new Dictionary<string, string>(StringComparer.Ordinal)),
            ]);

    public static ModelRouter Route(ProviderModel model)
    {
        var catalogModel = model.Variant is null
            ? model.Model
            : model.Model with
            {
                Capabilities = model.Model.Capabilities with
                {
                    Variants = [.. model.Model.Capabilities.Variants, model.Variant],
                },
            };
        var registry = new ProviderRegistry(
            [model.Provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                [model.Provider.Id] = [catalogModel],
            });
        return new ModelRouter(registry, new ModelAliasCatalog(registry, []), model.Selector);
    }

    public static ResolvedModelSelection Resolve(ProviderModel model) => Route(model).Resolve(model.Selector);
}
