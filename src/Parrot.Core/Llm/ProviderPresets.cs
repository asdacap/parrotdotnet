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
                ModelDefaults = [LLMModel.Create(
                    "kimi-for-coding", "kimi-code", "Kimi For Coding", 262144, 0, ModelCapabilities.Create(tools: true, reasoning: true, []))],
            },
            ["kimi-api"] = new ProviderPreset
            {
                Protocol = CompatibleProtocol.ChatCompletions,
                BaseUrl = "https://api.moonshot.ai/v1",
                ApiKeyEnv = "MOONSHOT_API_KEY",
                ModelDefaults =
                [
                    LLMModel.Create("kimi-k2-thinking", "kimi-api", "Kimi K2 Thinking", 262144, 32768, ModelCapabilities.Create(tools: true, reasoning: true, [])),
                    LLMModel.Create("kimi-k2-turbo-preview", "kimi-api", "Kimi K2 Turbo", 262144, 32768, ModelCapabilities.Create(tools: true, reasoning: false, [])),
                    LLMModel.Create("kimi-k2-0905-preview", "kimi-api", "Kimi K2 0905", 262144, 32768, ModelCapabilities.Create(tools: true, reasoning: false, [])),
                ],
            },
        };

        return presets;
    }

    private static IReadOnlyList<LLMModel> OpenCodeGoModels() =>
    [
        LLMModel.Create("minimax-m3", OpenCodeGo, "MiniMax M3", 1048576, 512000, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("minimax-m2.7", OpenCodeGo, "MiniMax M2.7", 204800, 131072, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("minimax-m2.5", OpenCodeGo, "MiniMax M2.5", 204800, 196608, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("kimi-k3", OpenCodeGo, "Kimi K3", 1048576, 0, ModelCapabilities.Create(tools: true, reasoning: true, ["max", "high", "low"])),
        LLMModel.Create("kimi-k2.7-code", OpenCodeGo, "Kimi K2.7 Code", 262144, 262144, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("kimi-k2.6", OpenCodeGo, "Kimi K2.6", 262144, 262144, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("kimi-k2.5", OpenCodeGo, "Kimi K2.5", 262144, 262144, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("glm-5.2", OpenCodeGo, "GLM 5.2", 1048576, 131072, ModelCapabilities.Create(tools: true, reasoning: true, ["xhigh", "high"])),
        LLMModel.Create("glm-5.1", OpenCodeGo, "GLM 5.1", 202752, 128000, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("glm-5", OpenCodeGo, "GLM 5", 204800, 131072, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("deepseek-v4-pro", OpenCodeGo, "DeepSeek V4 Pro", 1048576, 384000, ModelCapabilities.Create(tools: true, reasoning: true, ["xhigh", "high"])),
        LLMModel.Create("deepseek-v4-flash", OpenCodeGo, "DeepSeek V4 Flash", 1048576, 0, ModelCapabilities.Create(tools: true, reasoning: true, ["xhigh", "high"])),
        LLMModel.Create("qwen3.7-max", OpenCodeGo, "Qwen3.7 Max", 1000000, 65536, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("qwen3.7-plus", OpenCodeGo, "Qwen3.7 Plus", 1000000, 65536, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("qwen3.6-plus", OpenCodeGo, "Qwen3.6 Plus", 1000000, 65536, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("qwen3.5-plus", OpenCodeGo, "Qwen3.5 Plus", 1000000, 65536, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("mimo-v2.5-pro", OpenCodeGo, "MiMo V2.5 Pro", 1048576, 131072, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("mimo-v2.5", OpenCodeGo, "MiMo V2.5", 1048576, 131072, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("mimo-v2-pro", OpenCodeGo, "MiMo V2 Pro", 0, 0, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("mimo-v2-omni", OpenCodeGo, "MiMo V2 Omni", 0, 0, ModelCapabilities.Create(tools: true, reasoning: false, [])),
        LLMModel.Create("hy3-preview", OpenCodeGo, "Hy3 Preview", 262144, 0, ModelCapabilities.Create(tools: true, reasoning: true, ["high", "low", "none"])),
        LLMModel.Create("grok-4.5", OpenCodeGo, "Grok 4.5", 500000, 0, ModelCapabilities.Create(tools: true, reasoning: true, ["high", "medium", "low"])),
    ];
}
