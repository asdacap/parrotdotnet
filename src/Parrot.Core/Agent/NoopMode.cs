using Parrot.Security;

namespace Parrot.Agent;

internal sealed class NoopMode(IAgentProfile profile, SecurityProfile securityProfile) : IMode
{
    public IAgentProfile Profile { get; } = new SessionModeProfile(
        profile ?? throw new ArgumentNullException(nameof(profile)),
        () => profile.Prompt,
        securityProfile ?? throw new ArgumentNullException(nameof(securityProfile)));

    public void Prepare()
    {
    }

    public ModeCompletionOutcome Complete() => ModeCompletionOutcome.None;
}
