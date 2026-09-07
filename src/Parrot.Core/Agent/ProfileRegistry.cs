using Parrot.Config;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed class ProfileRegistry
{
    private readonly IReadOnlyDictionary<string, IAgentProfile> _profiles;

    public ProfileRegistry(
        IReadOnlyDictionary<string, ProfileConfig> profiles,
        IReadOnlyList<SandboxRule> globalRules,
        IReadOnlyList<SandboxRule> mandatoryRules,
        IReadOnlySet<string> disabledTools)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(globalRules);
        ArgumentNullException.ThrowIfNull(mandatoryRules);
        ArgumentNullException.ThrowIfNull(disabledTools);
        _profiles = profiles.ToDictionary(
            item => !string.IsNullOrWhiteSpace(item.Key)
                ? item.Key
                : throw new ArgumentException("Agent profile IDs must not be blank.", nameof(profiles)),
            item => (IAgentProfile)new AgentProfile(
                item.Key,
                item.Value ?? throw new ArgumentException("Agent profiles must not be null.", nameof(profiles)),
                globalRules,
                mandatoryRules,
                disabledTools),
            StringComparer.Ordinal);
    }

    public IReadOnlyList<IAgentProfile> Children => [.. _profiles.Values
        .Where(profile => profile.IsAgentSelectable)
        .OrderBy(profile => profile.Id, StringComparer.Ordinal)];

    public IReadOnlyList<string> UserSelectableIds => [.. _profiles.Values
        .Where(profile => profile.IsUserSelectable)
        .OrderBy(profile => profile.Id, StringComparer.Ordinal)
        .Select(profile => profile.Id)];

    public IAgentProfile ResolveChild(string id)
    {
        var selected = string.Equals(id, "explore", StringComparison.Ordinal) &&
                       _profiles.TryGetValue("explorer", out var explorer) &&
                       explorer.IsAgentSelectable
            ? "explorer"
            : id;
        var profile = Resolve(selected);
        return profile.IsAgentSelectable
            ? profile
            : throw new AgentRegistryException($"agent profile {id} cannot be spawned");
    }

    internal IAgentProfile Resolve(string id) => _profiles.GetValueOrDefault(id)
        ?? throw new AgentRegistryException($"unknown agent profile {id}");
}
