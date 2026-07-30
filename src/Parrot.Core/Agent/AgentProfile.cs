using Parrot.Config;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed class AgentProfile
{
    private readonly string[]? _allowedTools;
    private readonly string[] _disabledTools;

    public AgentProfile(
        string id,
        ProfileConfig configuration,
        IReadOnlyList<SandboxRule> globalRules,
        IReadOnlySet<string> disabledTools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(globalRules);
        ArgumentNullException.ThrowIfNull(disabledTools);
        Id = id;
        Prompt = configuration.Prompt;
        Usage = configuration.Usage;
        _allowedTools = configuration.AllowedTools is null ? null : [.. configuration.AllowedTools];
        _disabledTools = [.. disabledTools];
        MaxTurns = configuration.MaxTurns;
        RecursionLimit = configuration.RecursionLimit;
        ReadOnly = configuration.ReadOnly;
        IsUserAgent = configuration.IsUserAgent;
        SecurityProfile = SecurityProfile.Compose(ReadOnly, configuration.SandboxRules, globalRules, []);
    }

    public string Id { get; }

    public string Prompt { get; }

    public string Usage { get; }

    public IReadOnlyList<string>? AllowedTools => _allowedTools is null ? null : [.. _allowedTools];

    public IReadOnlyList<string> DisabledTools => [.. _disabledTools];

    public int MaxTurns { get; }

    public int RecursionLimit { get; }

    public bool ReadOnly { get; }

    public bool IsUserAgent { get; }

    public SecurityProfile SecurityProfile { get; }

    public MainAgentProfile BuildChildSessionProfile() => new(
        this,
        () => Prompt,
        static () => string.Empty,
        SecurityProfile,
        static () => { },
        static (_, _) => null);
}
