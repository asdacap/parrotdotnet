namespace Parrot.Agent;

internal sealed class ModeProfile(
    string id,
    string prompt,
    string hardRule,
    string status,
    int maxToolRounds,
    bool readOnly,
    string planArtifact,
    Action prepare)
{
    public string Id { get; } = id;

    public string Prompt { get; } = prompt;

    public string HardRule { get; } = hardRule;

    public string Status { get; } = status;

    public int MaxToolRounds { get; } = maxToolRounds;

    public bool ReadOnly { get; } = readOnly;

    public string PlanArtifact { get; } = planArtifact;

    public void Prepare() => prepare();
}
