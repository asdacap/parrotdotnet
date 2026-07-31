using Parrot.Protocol;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed class SessionMode(
    IAgentProfile profile,
    Func<string> prompt,
    SecurityProfile securityProfile,
    Action prepare,
    Func<string, string, PlanCompleted?> complete) : IMode
{
    public string Id => profile.Id;

    public string Prompt => prompt();

    public IReadOnlyList<string>? AllowedTools => profile.AllowedTools;

    public IReadOnlyList<string> DisabledTools => profile.DisabledTools;

    public int MaxTurns => profile.MaxTurns;

    public SecurityProfile SecurityProfile { get; } = securityProfile;

    public void Prepare() => prepare();

    public PlanCompleted? Complete(string sessionId, string messageId) => complete(sessionId, messageId);
}
