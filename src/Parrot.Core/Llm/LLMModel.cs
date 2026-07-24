namespace Parrot.Llm;

internal sealed record LLMModel(string Id, string ProviderId)
{
    public string Name { get; init; } = string.Empty;

    public int ContextWindow { get; init; }

    public int MaxOutputTokens { get; init; }

    // USD per token.
    public double InputPrice { get; init; }

    public double OutputPrice { get; init; }

    public ModelCapabilities Capabilities { get; init; } = ModelCapabilities.None;
}
