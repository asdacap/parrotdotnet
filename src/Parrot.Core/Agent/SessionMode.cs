using Parrot.Security;

namespace Parrot.Agent;

internal sealed class SessionMode(
    IAgentProfile profile,
    Func<string> prompt,
    SecurityProfile securityProfile,
    Action prepare,
    Func<string, string, ModeCompletionOutcome> complete) : IMode
{
    public string Id => profile.Id;

    public string Prompt => prompt();

    public IReadOnlyList<string>? AllowedTools => profile.AllowedTools;

    public IReadOnlyList<string> DisabledTools => profile.DisabledTools;

    public int MaxTurns => profile.MaxTurns;

    public bool EnforceActiveWorkCompletion => profile.EnforceActiveWorkCompletion;

    public SecurityProfile SecurityProfile { get; } = securityProfile;

    public void Prepare() => prepare();

    public ModeCompletionOutcome Complete(string sessionId, string messageId) => complete(sessionId, messageId);
}
