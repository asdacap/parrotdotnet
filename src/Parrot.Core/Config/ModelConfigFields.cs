namespace Parrot.Config;

[Flags]
internal enum ModelConfigFields
{
    None = 0,
    Name = 1 << 0,
    Context = 1 << 1,
    MaxTokens = 1 << 2,
    InputPrice = 1 << 3,
    CachedInputPrice = 1 << 4,
    OutputPrice = 1 << 5,
    Tools = 1 << 6,
    Reasoning = 1 << 7,
    Output = 1 << 8,
    Variants = 1 << 9,
}
