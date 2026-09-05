namespace Parrot.Skills;

internal sealed record SkillRoot(string Path, SkillScope Scope, bool FollowDirectoryLinks);
