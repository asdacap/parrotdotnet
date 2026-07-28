using Parrot.Config;
using Parrot.Protocol;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed class MainAgentProfile(
    string id,
    ProfileConfig configuration,
    Func<string> prompt,
    Func<string> planArtifact,
    SecurityProfile securityProfile,
    Action prepare,
    Func<string, string, PlanCompleted?> complete)
{
    public string Id { get; } = id;

    public string Prompt => prompt();

    public string HardRule { get; } = configuration.HardRule;

    public string Status { get; } = configuration.Status;

    public int MaxToolRounds { get; } = configuration.MaxToolRounds;

    public bool ReadOnly { get; } = configuration.ReadOnly;

    public string PlanArtifact => planArtifact();

    public SecurityProfile SecurityProfile { get; } = securityProfile;

    public void Prepare() => prepare();

    public PlanCompleted? Complete(string sessionId, string messageId) => complete(sessionId, messageId);
}
