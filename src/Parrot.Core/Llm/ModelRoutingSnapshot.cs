namespace Parrot.Llm;

internal sealed record ModelRoutingSnapshot(
    string ConfiguredDefaultSelector,
    ModelAliasSnapshot Aliases,
    long Revision);
