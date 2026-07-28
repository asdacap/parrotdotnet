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

        if (selected == Plan)
        {
            var artifact = Path.Combine(planDirectory, $"{sessionId}.md");
            return ModeProfile.Plan(artifact, () => PreparePlan(artifact));
        }

        return selected switch
        {
            Build => ModeProfile.Build(),
            Query => ModeProfile.Query(),
            _ => throw new ModeRegistryException($"unknown mode {selected}"),
        };
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
