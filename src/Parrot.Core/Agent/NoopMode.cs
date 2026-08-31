using Parrot.Security;

namespace Parrot.Agent;

internal sealed class NoopMode(IAgentProfile profile, SecurityProfile securityProfile) : IMode
{
    private readonly IAgentProfile _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    public string Id => _profile.Id;

    public string Prompt => _profile.Prompt;

    public IReadOnlyList<string>? AllowedTools => _profile.AllowedTools;

    public IReadOnlyList<string> DisabledTools => _profile.DisabledTools;

    public int MaxTurns => _profile.MaxTurns;

    public bool EnforceActiveWorkCompletion => _profile.EnforceActiveWorkCompletion;

    public SecurityProfile SecurityProfile { get; } = securityProfile
        ?? throw new ArgumentNullException(nameof(securityProfile));

    public void Prepare()
    {
    }

    public ModeCompletionOutcome Complete(string sessionId, string messageId) => ModeCompletionOutcome.None;
}
