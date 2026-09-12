namespace Parrot.Llm;

// Optional model behaviour callers may rely on when planning a request.
internal sealed record ModelCapabilities(
    bool Tools,
    bool Reasoning,
    IReadOnlyList<string> Output,
    IReadOnlyList<ModelVariant> Variants)
{
    public static ModelCapabilities None { get; } = new(false, false, [], []);

    public static ModelCapabilities Create(bool tools, bool reasoning, IReadOnlyList<string> efforts)
    {
        var variants = efforts.Select(effort => new ModelVariant(effort, effort)).ToList();
        return new(tools, reasoning || variants.Count > 0, ["text"], variants);
    }

    public ModelVariant? Variant(string name) =>
        Variants.FirstOrDefault(variant => variant.Name == name);
}
