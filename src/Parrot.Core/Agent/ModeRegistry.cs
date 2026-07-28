using Parrot.Config;
using Parrot.Security;

namespace Parrot.Agent;

internal sealed class ModeRegistry(
    string planDirectory,
    IReadOnlyList<SandboxRule> globalRules,
    IReadOnlyDictionary<string, ProfileSecurityConfig> profiles)
{
    public const string Build = "build";
    public const string Plan = "plan";
    public const string Query = "query";

    private readonly IReadOnlyList<string> _modeIds = [Build, Plan, Query];

    public ModeRegistry(string planDirectory)
        : this(planDirectory, [], new Dictionary<string, ProfileSecurityConfig>(StringComparer.Ordinal))
    {
    }

    public IReadOnlyList<string> List() => _modeIds;

    public ModeProfile Resolve(string id, string sessionId)
    {
        var selected = id.Length == 0 ? Build : id;

        if (selected == Plan)
        {
            var artifact = Path.Combine(planDirectory, $"{sessionId}.md");
            var configured = Profile(Plan);
            return ModeProfile.Plan(
                artifact,
                configured?.ReadOnly ?? true,
                configured?.SandboxRules ?? [],
                globalRules,
                () => PreparePlan(artifact));
        }

        return selected switch
        {
            Build => BuildProfile(),
            Query => QueryProfile(),
            _ => throw new ModeRegistryException($"unknown mode {selected}"),
        };
    }

    private ModeProfile BuildProfile()
    {
        var configured = Profile(Build);
        return ModeProfile.Build(
            configured?.ReadOnly ?? false,
            configured?.SandboxRules ?? [],
            globalRules);
    }

    private ModeProfile QueryProfile()
    {
        var configured = Profile(Query);
        return ModeProfile.Query(
            configured?.ReadOnly ?? true,
            configured?.SandboxRules ?? [],
            globalRules);
    }

    private ProfileSecurityConfig? Profile(string id) => profiles.GetValueOrDefault(id);

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
