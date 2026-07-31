namespace Parrot.Llm;

[Flags]
internal enum ModelMetadataFields
{
    None = 0,
    Name = 1 << 0,
    ContextWindow = 1 << 1,
    MaxOutputTokens = 1 << 2,
    InputPrice = 1 << 3,
    OutputPrice = 1 << 4,
    Tools = 1 << 5,
    Reasoning = 1 << 6,
    Output = 1 << 7,
    Variants = 1 << 8,
}
