using Parrot.Security;

namespace Parrot.Agent;

internal sealed class SessionModeProfile(
    IAgentProfile profile,
    Func<string> prompt,
    SecurityProfile securityProfile) : IAgentProfile
{
    public string Id => profile.Id;

    public string Usage => profile.Usage;

    public int RecursionLimit => profile.RecursionLimit;

    public string Prompt => prompt();

    public IReadOnlyList<string>? AllowedTools => profile.AllowedTools;

    public IReadOnlyList<string> DisabledTools => profile.DisabledTools;

    public int MaxTurns => profile.MaxTurns;

    public bool EnforceActiveWorkCompletion => profile.EnforceActiveWorkCompletion;

    public bool IsUserSelectable => profile.IsUserSelectable;

    public bool IsAgentSelectable => profile.IsAgentSelectable;

    public SecurityProfile SecurityProfile { get; } = securityProfile;

    public bool IsToolPermitted(string toolName) => profile.IsToolPermitted(toolName);
}
