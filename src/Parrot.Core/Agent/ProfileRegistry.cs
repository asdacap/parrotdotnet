using Parrot.Config;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed class ProfileRegistry
{
    private readonly IReadOnlyDictionary<string, AgentProfile> _profiles;
    private readonly string _defaultProfile;

    public ProfileRegistry(
        IReadOnlyDictionary<string, ProfileConfig> profiles,
        IReadOnlyList<SandboxRule> globalRules,
        IReadOnlySet<string> disabledTools,
        string defaultProfile)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(globalRules);
        ArgumentNullException.ThrowIfNull(disabledTools);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProfile);
        _profiles = profiles.ToDictionary(
            item => !string.IsNullOrWhiteSpace(item.Key)
                ? item.Key
                : throw new ArgumentException("Agent profile IDs must not be blank.", nameof(profiles)),
            item => new AgentProfile(
                item.Key,
                item.Value ?? throw new ArgumentException("Agent profiles must not be null.", nameof(profiles)),
                globalRules,
                disabledTools),
            StringComparer.Ordinal);
        _defaultProfile = defaultProfile;
        _ = ResolveForeground(string.Empty);
    }

    public IReadOnlyList<AgentProfile> Foreground => List(true);

    public IReadOnlyList<AgentProfile> Children => List(false);

    public AgentProfile ResolveForeground(string id)
    {
        var selected = id.Length == 0 ? _defaultProfile : id;

        if (!_profiles.TryGetValue(selected, out var profile) || !profile.IsUserAgent)
        {
            throw new ModeRegistryException($"unknown mode {selected}");
        }

        return profile;
    }

    public AgentProfile ResolveChild(string id)
    {
        var selected = string.Equals(id, "explore", StringComparison.Ordinal) ? "explorer" : id;
        var profile = Resolve(selected);
        return !profile.IsUserAgent
            ? profile
            : throw new AgentRegistryException($"agent profile {id} cannot be spawned");
    }

    private AgentProfile Resolve(string id) => _profiles.GetValueOrDefault(id)
        ?? throw new AgentRegistryException($"unknown agent profile {id}");

    private IReadOnlyList<AgentProfile> List(bool userAgent) => [.. _profiles.Values
        .Where(profile => profile.IsUserAgent == userAgent)
        .OrderBy(profile => profile.Id, StringComparer.Ordinal)];
}
