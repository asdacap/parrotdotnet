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

    public static ModeProfile Build() =>
        new(
            ModeRegistry.Build,
            "You are Parrot's build mode. Implement and verify the requested changes.",
            "Keep tool side effects within the authorized workspace.",
            "Build mode: implement and verify requested changes. Workspace writes are permitted through the active security policy.",
            64,
            readOnly: false,
            string.Empty,
            static () => { });

    public static ModeProfile Query() =>
        new(
            ModeRegistry.Query,
            "You are Parrot's query mode. Inspect the project and answer the user's question without making changes.",
            "Read-only mode: do not modify the workspace.",
            "Query mode: inspect the project and answer questions without changing files.",
            24,
            readOnly: true,
            string.Empty,
            static () => { });

    public static ModeProfile Plan(string artifact, Action prepare) =>
        new(
            ModeRegistry.Plan,
            $"You are Parrot's plan mode. Inspect the project and write the complete implementation plan as Markdown to this exact file: {artifact}. Do not include the plan in your assistant response.",
            "The designated plan artifact is the only writable path; do not modify other workspace files.",
            "Plan mode: inspect the project and write the designated plan artifact without changing other files.",
            24,
            readOnly: true,
            artifact,
            prepare);

    public void Prepare() => prepare();
}
