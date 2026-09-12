using Parrot.Context;

namespace Parrot.Config;

internal sealed record ModelAliasConfig(
    string ModelString,
    string Usage,
    string? AugmentSystemPrompt,
    ModelAliasIconConfig? Icon)
{
    public ContextSize? ContextLimit { get; init; }
}
