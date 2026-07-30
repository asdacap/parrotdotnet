namespace Parrot.Agent;

internal sealed class ModeRegistry(ProfileRegistry profiles)
{
    public const string Build = "build";
    public const string Plan = "plan";
    public const string Query = "query";

    private readonly IReadOnlyList<string> _modeIds = [.. profiles.Foreground.Select(profile => profile.Id)];

    public ProfileRegistry Profiles => profiles;

    public string Default => Resolve(string.Empty).Id;

    public IReadOnlyList<string> List() => _modeIds;

    public AgentProfile Resolve(string id) => profiles.ResolveForeground(id);
}
