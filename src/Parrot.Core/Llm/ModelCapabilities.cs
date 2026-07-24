namespace Parrot.Llm;

// Optional model behaviour callers may rely on when planning a request.
internal sealed record ModelCapabilities(
    bool Tools,
    bool Reasoning,
    IReadOnlyList<string> Output,
    IReadOnlyList<ModelVariant> Variants)
{
    public static ModelCapabilities None { get; } = new(false, false, [], []);

    public ModelVariant? Variant(string name) =>
        Variants.FirstOrDefault(variant => variant.Name == name);
}
