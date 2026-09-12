namespace Parrot.Skills;

internal sealed record SkillMetadata(
    string Name,
    string Description,
    string Path,
    SkillScope Scope,
    string? DisplayName,
    string? ShortDescription,
    bool Enabled,
    bool PromptVisible)
{
    public string DiscoveryPath { get; init; } = Path;

    public string EffectiveDescription => ShortDescription ?? Description;
}
