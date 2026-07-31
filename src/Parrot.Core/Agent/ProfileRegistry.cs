using Parrot.Config;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed class ProfileRegistry
{
    private static readonly HashSet<string> ModeIds = new(
        [ModeRegistry.Build, ModeRegistry.Plan, ModeRegistry.Query],
        StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, AgentProfile> _profiles;

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
            item => new AgentProfile(
                item.Key,
                item.Value ?? throw new ArgumentException("Agent profiles must not be null.", nameof(profiles)),
                globalRules,
                mandatoryRules,
                disabledTools),
            StringComparer.Ordinal);
    }

    public IReadOnlyList<AgentProfile> Children => [.. _profiles.Values
        .Where(profile => !ModeIds.Contains(profile.Id))
        .OrderBy(profile => profile.Id, StringComparer.Ordinal)];

    public AgentProfile ResolveChild(string id)
    {
        var selected = string.Equals(id, "explore", StringComparison.Ordinal) ? "explorer" : id;
        var profile = Resolve(selected);
        return !ModeIds.Contains(profile.Id)
            ? profile
            : throw new AgentRegistryException($"agent profile {id} cannot be spawned");
    }

    internal AgentProfile Resolve(string id) => _profiles.GetValueOrDefault(id)
        ?? throw new AgentRegistryException($"unknown agent profile {id}");
}
