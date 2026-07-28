using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal static class TestModels
{
    public static IReadOnlyDictionary<string, ProfileConfig> Profiles { get; } =
        new Dictionary<string, ProfileConfig>(StringComparer.Ordinal)
        {
            [ModeRegistry.Build] = new()
            {
                Prompt = "You are Parrot's build mode. Implement and verify the requested changes.",
                HardRule = "Keep tool side effects within the authorized workspace.",
                Status = "Build mode: implement and verify requested changes. Workspace writes are permitted through the active security policy.",
                MaxToolRounds = 64,
                ReadOnly = false,
            },
            [ModeRegistry.Plan] = new()
            {
                Prompt = "You are Parrot's plan mode. Inspect the project and write the complete implementation plan as Markdown",
                HardRule = "The plan directory is the only writable location; do not modify workspace files.",
                Status = "Plan mode: inspect the project and write the designated plan artifact without changing other files.",
                MaxToolRounds = 24,
                ReadOnly = true,
            },
            [ModeRegistry.Query] = new()
            {
                Prompt = "You are Parrot's query mode. Inspect the project and answer the user's question without making changes.",
                HardRule = "Read-only mode: do not modify the workspace.",
                Status = "Query mode: inspect the project and answer questions without changing files.",
                MaxToolRounds = 24,
                ReadOnly = true,
            },
        };

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
