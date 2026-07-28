using Parrot.Security;

namespace Parrot.Agent;

internal sealed class ModeProfile(
    string id,
    string prompt,
    string hardRule,
    string status,
    int maxToolRounds,
    bool readOnly,
    string planArtifact,
    SecurityProfile securityProfile,
    Action prepare)
{
    public string Id { get; } = id;

    public string Prompt { get; } = prompt;

    public string HardRule { get; } = hardRule;

    public string Status { get; } = status;

    public int MaxToolRounds { get; } = maxToolRounds;

    public bool ReadOnly { get; } = readOnly;

    public string PlanArtifact { get; } = planArtifact;

    public SecurityProfile SecurityProfile { get; } = securityProfile;

    public static ModeProfile Build(
        bool readOnly,
        IReadOnlyList<SandboxRule> modeRules,
        IReadOnlyList<SandboxRule> globalRules) =>
        new(
            ModeRegistry.Build,
            "You are Parrot's build mode. Implement and verify the requested changes.",
            "Keep tool side effects within the authorized workspace.",
            "Build mode: implement and verify requested changes. Workspace writes are permitted through the active security policy.",
            64,
            readOnly,
            string.Empty,
            SecurityProfile.Compose(readOnly, modeRules, globalRules, []),
            static () => { });

    public static ModeProfile Query(
        bool readOnly,
        IReadOnlyList<SandboxRule> modeRules,
        IReadOnlyList<SandboxRule> globalRules) =>
        new(
            ModeRegistry.Query,
            "You are Parrot's query mode. Inspect the project and answer the user's question without making changes.",
            "Read-only mode: do not modify the workspace.",
            "Query mode: inspect the project and answer questions without changing files.",
            24,
            readOnly,
            string.Empty,
            SecurityProfile.Compose(readOnly, modeRules, globalRules, []),
            static () => { });

    public static ModeProfile Plan(
        string directory,
        string artifact,
        bool readOnly,
        IReadOnlyList<SandboxRule> modeRules,
        IReadOnlyList<SandboxRule> globalRules,
        Action prepare) =>
        new(
            ModeRegistry.Plan,
            $"You are Parrot's plan mode. Inspect the project and write the complete implementation plan as Markdown to this exact file: {artifact}. You may write optional supporting artifacts under this plan directory and reference them from the canonical plan: {directory}. Do not include the plan in your assistant response. Finish only after writing the canonical file.",
            "The plan directory is the only writable location; do not modify workspace files.",
            "Plan mode: inspect the project and write the designated plan artifact without changing other files.",
            24,
            readOnly,
            artifact,
            SecurityProfile.Compose(
                readOnly,
                modeRules,
                globalRules,
                [new SandboxRule(directory, SandboxRuleAction.AllowWrite)]),
            prepare);

    public void Prepare() => prepare();
}
