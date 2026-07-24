namespace Parrot.Llm;

// The built-in provider catalogue. Presets are not configuration: a project file
// cannot redirect a preset provider's base URL. Model metadata is a starting
// point a user may override per model. Port of Go's app/presets.go.
internal static class ProviderPresets
{
    private const string OpenCodeGo = "opencode-go";

    public static IReadOnlyDictionary<string, ProviderPreset> All { get; } = Build();

    public static IReadOnlyList<string> Ids() =>
        [.. All.Keys.OrderBy(id => id, StringComparer.Ordinal)];

    // Preset providers with a usable base URL that the configuration does not
    // mention. Built from the preset alone, so storing a credential is enough.
    public static IReadOnlyList<string> PresetOnlyIds(IEnumerable<string> configuredIds)
    {
        var configured = new HashSet<string>(configuredIds, StringComparer.Ordinal);

        return
        [
            .. All
                .Where(entry => entry.Value.BaseUrl.Length > 0 && !configured.Contains(entry.Key))
                .Select(entry => entry.Key)
                .OrderBy(id => id, StringComparer.Ordinal),
        ];
    }

    private static Dictionary<string, ProviderPreset> Build()
    {
        var presets = new Dictionary<string, ProviderPreset>(StringComparer.Ordinal)
        {
            ["openai"] = new ProviderPreset { HeaderTimeout = TimeSpan.FromSeconds(10) },
            ["openrouter"] = new ProviderPreset
            {
                Protocol = CompatibleProtocol.ChatCompletions,
                BaseUrl = "https://openrouter.ai/api/v1",
                ApiKeyEnv = "OPENROUTER_API_KEY",
                HeaderTimeout = TimeSpan.FromSeconds(10),
                Decoder = OpenRouterModelDecoder.Instance,
                SupportsProviderPreferences = true,
            },
            [OpenCodeGo] = new ProviderPreset
            {
                Protocol = CompatibleProtocol.ChatCompletions,
                BaseUrl = "https://opencode.ai/zen/go/v1",
                ApiKeyEnv = "OPENCODE_GO_API_KEY",
                HeaderTimeout = TimeSpan.FromSeconds(10),
                ModelDefaults = OpenCodeGoModels(),
            },
            ["kimi-code"] = new ProviderPreset
            {
                Protocol = CompatibleProtocol.ChatCompletions,
                BaseUrl = "https://api.kimi.com/coding/v1",
                ApiKeyEnv = "KIMI_API_KEY",
                Decoder = KimiModelDecoder.Instance,
                ModelDefaults = [Model("kimi-code", "kimi-for-coding", "Kimi For Coding", 262144, tools: true, reasoning: true)],
            },
            ["kimi-api"] = new ProviderPreset
            {
                Protocol = CompatibleProtocol.ChatCompletions,
                BaseUrl = "https://api.moonshot.ai/v1",
                ApiKeyEnv = "MOONSHOT_API_KEY",
                ModelDefaults =
                [
                    Model("kimi-api", "kimi-k2-thinking", "Kimi K2 Thinking", 262144, 32768, tools: true, reasoning: true),
                    Model("kimi-api", "kimi-k2-turbo-preview", "Kimi K2 Turbo", 262144, 32768, tools: true),
                    Model("kimi-api", "kimi-k2-0905-preview", "Kimi K2 0905", 262144, 32768, tools: true),
                ],
            },
        };

        return presets;
    }

    private static IReadOnlyList<LLMModel> OpenCodeGoModels() =>
    [
        Model(OpenCodeGo, "minimax-m3", "MiniMax M3", 1048576, 512000, tools: true),
        Model(OpenCodeGo, "minimax-m2.7", "MiniMax M2.7", 204800, 131072, tools: true),
        Model(OpenCodeGo, "minimax-m2.5", "MiniMax M2.5", 204800, 196608, tools: true),
        Model(OpenCodeGo, "kimi-k3", "Kimi K3", 1048576, tools: true, reasoning: true, variants: ["max", "high", "low"]),
        Model(OpenCodeGo, "kimi-k2.7-code", "Kimi K2.7 Code", 262144, 262144, tools: true),
        Model(OpenCodeGo, "kimi-k2.6", "Kimi K2.6", 262144, 262144, tools: true),
        Model(OpenCodeGo, "kimi-k2.5", "Kimi K2.5", 262144, 262144, tools: true),
        Model(OpenCodeGo, "glm-5.2", "GLM 5.2", 1048576, 131072, tools: true, reasoning: true, variants: ["xhigh", "high"]),
        Model(OpenCodeGo, "glm-5.1", "GLM 5.1", 202752, 128000, tools: true),
        Model(OpenCodeGo, "glm-5", "GLM 5", 204800, 131072, tools: true),
        Model(OpenCodeGo, "deepseek-v4-pro", "DeepSeek V4 Pro", 1048576, 384000, tools: true, reasoning: true, variants: ["xhigh", "high"]),
        Model(OpenCodeGo, "deepseek-v4-flash", "DeepSeek V4 Flash", 1048576, tools: true, reasoning: true, variants: ["xhigh", "high"]),
        Model(OpenCodeGo, "qwen3.7-max", "Qwen3.7 Max", 1000000, 65536, tools: true),
        Model(OpenCodeGo, "qwen3.7-plus", "Qwen3.7 Plus", 1000000, 65536, tools: true),
        Model(OpenCodeGo, "qwen3.6-plus", "Qwen3.6 Plus", 1000000, 65536, tools: true),
        Model(OpenCodeGo, "qwen3.5-plus", "Qwen3.5 Plus", 1000000, 65536, tools: true),
        Model(OpenCodeGo, "mimo-v2.5-pro", "MiMo V2.5 Pro", 1048576, 131072, tools: true),
        Model(OpenCodeGo, "mimo-v2.5", "MiMo V2.5", 1048576, 131072, tools: true),
        Model(OpenCodeGo, "mimo-v2-pro", "MiMo V2 Pro", tools: true),
        Model(OpenCodeGo, "mimo-v2-omni", "MiMo V2 Omni", tools: true),
        Model(OpenCodeGo, "hy3-preview", "Hy3 Preview", 262144, tools: true, reasoning: true, variants: ["high", "low", "none"]),
        Model(OpenCodeGo, "grok-4.5", "Grok 4.5", 500000, tools: true, reasoning: true, variants: ["high", "medium", "low"]),
    ];

    private static LLMModel Model(
        string providerId,
        string id,
        string name,
        int context = 0,
        int maxTokens = 0,
        bool tools = false,
        bool reasoning = false,
        string[]? variants = null)
    {
        var built = (variants ?? []).Select(effort => new ModelVariant(effort, effort)).ToList();

        return new LLMModel(id, providerId)
        {
            Name = name,
            ContextWindow = context,
            MaxOutputTokens = maxTokens,
            Capabilities = new ModelCapabilities(tools, reasoning || built.Count > 0, ["text"], built),
        };
    }
}
