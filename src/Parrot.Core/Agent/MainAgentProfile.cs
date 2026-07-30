using Parrot.Protocol;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed class MainAgentProfile(
    AgentProfile profile,
    Func<string> prompt,
    Func<string> planArtifact,
    SecurityProfile securityProfile,
    Action prepare,
    Func<string, string, PlanCompleted?> complete)
{
    public string Id => profile.Id;

    public string Prompt => prompt();

    public IReadOnlyList<string>? AllowedTools => profile.AllowedTools;

    public IReadOnlyList<string> DisabledTools => profile.DisabledTools;

    public int MaxTurns => profile.MaxTurns;

    public int RecursionLimit => profile.RecursionLimit;

    public bool ReadOnly => profile.ReadOnly;

    public bool IsUserAgent => profile.IsUserAgent;

    public string Usage => profile.Usage;

    public string PlanArtifact => planArtifact();

    public SecurityProfile SecurityProfile { get; } = securityProfile;

    public void Prepare() => prepare();

    public PlanCompleted? Complete(string sessionId, string messageId) => complete(sessionId, messageId);
}
