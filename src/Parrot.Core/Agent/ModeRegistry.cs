using Parrot.Config;
using Parrot.Protocol;
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

    public MainAgentProfile Resolve(string id, string sessionId)
    {
        var selected = id.Length == 0 ? Build : id;

        if (selected == Plan)
        {
            var configured = Profile(Plan);
            return MainAgentProfile.Plan(
                planDirectory,
                () => PlanArtifact(sessionId),
                configured?.ReadOnly ?? true,
                configured?.SandboxRules ?? [],
                globalRules,
                () => PreparePlan(sessionId),
                (agentSessionId, messageId) => CompletePlan(sessionId, agentSessionId, messageId));
        }

        return selected switch
        {
            Build => BuildProfile(),
            Query => QueryProfile(),
            _ => throw new ModeRegistryException($"unknown mode {selected}"),
        };
    }

    private void SecureArtifact(string artifact)
    {
        SecureDirectory();

        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                File.SetUnixFileMode(artifact, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new ModeRegistryException($"mode: make plan file writable: {failure.Message}");
        }
    }

    private MainAgentProfile BuildProfile()
    {
        var configured = Profile(Build);
        return MainAgentProfile.Build(
            configured?.ReadOnly ?? false,
            configured?.SandboxRules ?? [],
            globalRules);
    }

    private MainAgentProfile QueryProfile()
    {
        var configured = Profile(Query);
        return MainAgentProfile.Query(
            configured?.ReadOnly ?? true,
            configured?.SandboxRules ?? [],
            globalRules);
    }

    private ProfileSecurityConfig? Profile(string id) => profiles.GetValueOrDefault(id);

    private string PlanArtifact(string sessionId)
    {
        lock (_planGate)
        {
            return _planArtifacts.GetValueOrDefault(sessionId, string.Empty);
        }
    }

    private void PreparePlan(string sessionId)
    {
        lock (_planGate)
        {
            if (_planArtifacts.TryGetValue(sessionId, out var existing))
            {
                SecureArtifact(existing);
                return;
            }

            try
            {
                _ = Directory.CreateDirectory(planDirectory);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                throw new ModeRegistryException($"mode: create plan directory: {failure.Message}");
            }

            SecureDirectory();
            var artifact = Path.Combine(planDirectory, $"plan-{Guid.NewGuid():n}.md");

            try
            {
                using var stream = new FileStream(artifact, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                throw new ModeRegistryException($"mode: create plan file: {failure.Message}");
            }

            SecureArtifact(artifact);
            _planArtifacts.Add(sessionId, artifact);
        }
    }

    private PlanCompleted? CompletePlan(string userSessionId, string agentSessionId, string messageId)
    {
        string plan;

        lock (_planGate)
        {
            if (!_planArtifacts.TryGetValue(userSessionId, out var artifact))
            {
                return null;
            }

            try
            {
                plan = File.ReadAllText(artifact).Trim();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                throw new ModeRegistryException($"mode: read plan: {failure.Message}");
            }
        }

        return plan.Length == 0
            ? null
            : new PlanCompleted
            {
                SessionId = agentSessionId,
                MessageId = messageId,
                Markdown = plan,
                Dialog = new TurnCompleteDialog
                {
                    Prompt = "Plan complete: ",
                    Context = { "Review the plan before implementation." },
                    Choices =
                    {
                        new DialogChoice
                        {
                            Value = "yes",
                            Description = "Implement the approved plan",
                            Aliases = { "y" },
                            Action = new ChoiceAction { Mode = Build, Prompt = "Implement the approved plan." },
                        },
                        new DialogChoice { Value = "no", Description = "Stop after planning", Aliases = { "n" } },
                    },
                    CustomChoice = "feedback",
                    CustomDescription = "Provide feedback and revise the plan",
                    CustomPrompt = "plan feedback: ",
                    EmptyMessage = "enter yes, no, or feedback",
                },
            };
    }

    private void SecureDirectory()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(planDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
