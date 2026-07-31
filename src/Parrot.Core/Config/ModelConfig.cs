namespace Parrot.Config;

// Per-model metadata a user may declare under a provider.
internal sealed record ModelConfig
{
    public string Name { get; init; } = string.Empty;

    public int Context { get; init; }

    public int MaxTokens { get; init; }

    public double InputPrice { get; init; }

    public double OutputPrice { get; init; }

    public bool Tools { get; init; }

    public bool Reasoning { get; init; }

    public IReadOnlyList<string> Output { get; init; } = [];

    // Variant name to reasoning effort.
    public IReadOnlyDictionary<string, string> Variants { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public ModelConfigFields Fields { get; init; }
}
