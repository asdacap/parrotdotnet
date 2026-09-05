namespace Parrot.Skills;

internal sealed record SkillSnapshot(
    IReadOnlyList<SkillMetadata> Skills,
    IReadOnlyList<SkillLoadError> Errors)
{
    public static SkillSnapshot Empty { get; } = new([], []);
}
