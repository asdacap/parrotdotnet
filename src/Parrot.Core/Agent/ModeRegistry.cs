namespace Parrot.Agent;

internal sealed class ModeRegistry(string planDirectory)
{
    public const string Build = "build";
    public const string Plan = "plan";
    public const string Query = "query";

    private readonly IReadOnlyList<string> _modeIds = [Build, Plan, Query];

    public IReadOnlyList<string> List() => _modeIds;

    public ModeProfile Resolve(string id, string sessionId)
    {
        var selected = id.Length == 0 ? Build : id;

        return selected switch
        {
            Build => new ModeProfile(
                Build,
                "You are Parrot's build mode. Implement and verify the requested changes.",
                "Keep tool side effects within the authorized workspace.",
                "Build mode: implement and verify requested changes. Workspace writes are permitted through the active security policy.",
                64,
                readOnly: false,
                string.Empty,
                static () => { }),
            Plan => PlanProfile(sessionId),
            Query => new ModeProfile(
                Query,
                "You are Parrot's query mode. Inspect the project and answer the user's question without making changes.",
                "Read-only mode: do not modify the workspace.",
                "Query mode: inspect the project and answer questions without changing files.",
                24,
                readOnly: true,
                string.Empty,
                static () => { }),
            _ => throw new ModeRegistryException($"unknown mode {selected}"),
        };
    }

    private ModeProfile PlanProfile(string sessionId)
    {
        var artifact = Path.Combine(planDirectory, $"{sessionId}.md");

        return new ModeProfile(
            Plan,
            $"You are Parrot's plan mode. Inspect the project and write the complete implementation plan as Markdown to this exact file: {artifact}. Do not include the plan in your assistant response.",
            "The designated plan artifact is the only writable path; do not modify other workspace files.",
            "Plan mode: inspect the project and write the designated plan artifact without changing other files.",
            24,
            readOnly: true,
            artifact,
            () => PreparePlan(artifact));
    }

    private void PreparePlan(string artifact)
    {
        _ = Directory.CreateDirectory(planDirectory);
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) &&
            File.Exists(artifact))
        {
            File.SetUnixFileMode(artifact, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var stream = new FileStream(artifact, FileMode.Create, FileAccess.Write, FileShare.Read);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(artifact, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
