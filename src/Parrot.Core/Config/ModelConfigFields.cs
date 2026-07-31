namespace Parrot.Config;

[Flags]
internal enum ModelConfigFields
{
    None = 0,
    Name = 1 << 0,
    Context = 1 << 1,
    MaxTokens = 1 << 2,
    InputPrice = 1 << 3,
    OutputPrice = 1 << 4,
    Tools = 1 << 5,
    Reasoning = 1 << 6,
    Output = 1 << 7,
    Variants = 1 << 8,
}
