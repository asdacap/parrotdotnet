using Parrot.Config;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed class AgentProfile : IAgentProfile
{
    private readonly string[]? _allowedTools;
    private readonly string[] _disabledTools;

    public AgentProfile(
        string id,
        ProfileConfig configuration,
        IReadOnlyList<SandboxRule> globalRules,
        IReadOnlyList<SandboxRule> mandatoryRules,
        IReadOnlySet<string> disabledTools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(globalRules);
        ArgumentNullException.ThrowIfNull(mandatoryRules);
        ArgumentNullException.ThrowIfNull(disabledTools);
        Id = id;
        Prompt = configuration.Prompt;
        Usage = configuration.Usage;
        _allowedTools = configuration.AllowedTools is null ? null : [.. configuration.AllowedTools];
        _disabledTools = [.. disabledTools];
        MaxTurns = configuration.MaxTurns;
        RecursionLimit = configuration.RecursionLimit;
        EnforceActiveWorkCompletion = configuration.EnforceActiveWorkCompletion;
        SecurityProfile = SecurityProfile.Compose(
            configuration.ReadOnly,
            configuration.SandboxRules,
            globalRules,
            mandatoryRules);
    }

    public string Id { get; }

    public string Prompt { get; }

    public string Usage { get; }

    public IReadOnlyList<string>? AllowedTools => _allowedTools is null ? null : [.. _allowedTools];

    public IReadOnlyList<string> DisabledTools => [.. _disabledTools];

    public int MaxTurns { get; }

    public int RecursionLimit { get; }

    public bool EnforceActiveWorkCompletion { get; }

    public SecurityProfile SecurityProfile { get; }
}
