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
            [ModeRegistry.Build] = Profile(
                "You are Parrot's build mode. Implement and verify the requested changes.",
                "Keep tool side effects within the authorized workspace.",
                readOnly: false),
            [ModeRegistry.Plan] = Profile(
                "You are Parrot's plan mode. Inspect the project and write the complete implementation plan as Markdown",
                "The plan directory is the only writable location; do not modify workspace files.",
                readOnly: true),
            [ModeRegistry.Query] = Profile(
                "You are Parrot's query mode. Inspect the project and answer the user's question without making changes.",
                "Read-only mode: do not modify the workspace.",
                readOnly: true),
            ["explorer"] = new ProfileConfig(
                "You are an explorer agent.",
                "Test read-only child profile.",
                ["Do not modify files."],
                null,
                32,
                3,
                true,
                false,
                []),
            ["worker"] = new ProfileConfig(
                "You are a worker agent.",
                "Test child profile.",
                ["Keep changes contained."],
                null,
                64,
                3,
                false,
                false,
                []),
        };

    public static ProfileRegistry ProfileRegistry() =>
        new(Profiles, [], new HashSet<string>(StringComparer.Ordinal), ModeRegistry.Build);

    public static ISystemPromptProvider PromptProvider(string workingDirectory, string configDirectory) =>
        new CompositeSystemPromptProvider(
            "test:system-prompt",
            [
                new SystemContextProvider(workingDirectory, configDirectory, "2026-07-24", ProfileRegistry()),
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

    private static ProfileConfig Profile(string prompt, string hardRule, bool readOnly) => new(
        prompt,
        "Test profile.",
        [hardRule],
        null,
        64,
        3,
        readOnly,
        true,
        []);
}
