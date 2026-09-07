using Parrot.Security;

namespace Parrot.Agent;

internal sealed class SessionMode(
    IAgentProfile profile,
    Func<string> prompt,
    SecurityProfile securityProfile,
    Action prepare,
    Func<string, string, ModeCompletionOutcome> complete) : IMode
{
    public IAgentProfile Profile { get; } = new SessionModeProfile(profile, prompt, securityProfile);

    public void Prepare() => prepare();

    public ModeCompletionOutcome Complete(string sessionId, string messageId) => complete(sessionId, messageId);
}
