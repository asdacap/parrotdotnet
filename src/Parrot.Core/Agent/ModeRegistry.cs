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
    private readonly Lock _planGate = new();
    private readonly Dictionary<string, string> _planArtifacts = new(StringComparer.Ordinal);

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
            var artifact = PlanArtifact(sessionId);
            var configured = Profile(Plan);
            return ModeProfile.Plan(
                planDirectory,
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

    private string PlanArtifact(string sessionId)
    {
        lock (_planGate)
        {
            if (_planArtifacts.TryGetValue(sessionId, out var existing))
            {
                return existing;
            }

            _ = Directory.CreateDirectory(planDirectory);
            SecureDirectory();
            var artifact = Path.Combine(planDirectory, $"plan-{Guid.NewGuid():n}.md");

            using (var stream = new FileStream(artifact, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
            }

            PreparePlan(artifact);
            _planArtifacts.Add(sessionId, artifact);
            return artifact;
        }
    }

    private void PreparePlan(string artifact)
    {
        SecureDirectory();

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(artifact, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private void SecureDirectory()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(planDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
