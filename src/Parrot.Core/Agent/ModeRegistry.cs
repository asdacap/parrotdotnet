namespace Parrot.Agent;

internal sealed class ModeRegistry
{
    public const string Build = "build";
    public const string Plan = "plan";
    public const string Query = "query";

    private readonly IReadOnlyList<string> _modes;
    private readonly ProfileRegistry _profiles;

    public ModeRegistry(ProfileRegistry profiles, string defaultProfile)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProfile);
        _profiles = profiles;
        _modes = [.. profiles.UserSelectableIds];
        Default = Resolve(defaultProfile).Id;
    }

    public string Default { get; }

    public IReadOnlyList<string> List() => _modes;

    public IAgentProfile Resolve(string id)
    {
        var selected = id.Length == 0 ? Default : id;
        if (!_modes.Contains(selected, StringComparer.Ordinal))
        {
            throw new ModeRegistryException($"unknown mode {selected}");
        }

        try
        {
            return _profiles.Resolve(selected);
        }
        catch (AgentRegistryException)
        {
            throw new ModeRegistryException($"unknown mode {selected}");
        }
    }
}
