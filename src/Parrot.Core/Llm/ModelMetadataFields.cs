namespace Parrot.Llm;

[Flags]
internal enum ModelMetadataFields
{
    None = 0,
    Name = 1 << 0,
    ContextWindow = 1 << 1,
    MaxOutputTokens = 1 << 2,
    MaxInputTokens = 1 << 10,
    InputPrice = 1 << 3,
    CachedInputPrice = 1 << 4,
    OutputPrice = 1 << 5,
    Tools = 1 << 6,
    Reasoning = 1 << 7,
    Output = 1 << 8,
    Variants = 1 << 9,
}
