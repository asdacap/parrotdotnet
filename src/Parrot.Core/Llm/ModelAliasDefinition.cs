namespace Parrot.Llm;

internal sealed record ModelAliasDefinition(
    string Name,
    string ModelString,
    string Usage,
    string? AugmentSystemPrompt);
