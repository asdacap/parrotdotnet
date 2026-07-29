using Parrot.Protocol;

namespace Parrot.Agent;

internal sealed class ModeRegistry(string planDirectory, ProfileRegistry profiles)
{
    public const string Build = "build";
    public const string Plan = "plan";
    public const string Query = "query";

    private readonly IReadOnlyList<string> _modeIds = [.. profiles.Foreground.Select(profile => profile.Id)];
    private readonly Lock _planGate = new();
    private readonly Dictionary<string, string> _planArtifacts = new(StringComparer.Ordinal);

    public ProfileRegistry Profiles => profiles;

    public string Default => profiles.ResolveForeground(string.Empty).Id;

    public IReadOnlyList<string> List() => _modeIds;

    public MainAgentProfile Resolve(string id, string sessionId)
    {
        var profile = profiles.ResolveForeground(id);

        return string.Equals(profile.Id, Plan, StringComparison.Ordinal)
            ? new MainAgentProfile(
                profile,
                () => $"{profile.Prompt} to this exact file: {PlanArtifact(sessionId)}. You may write optional supporting artifacts under this plan directory and reference them from the canonical plan: {planDirectory}. Do not include the plan in your assistant response. Finish only after writing the canonical file.",
                () => PlanArtifact(sessionId),
                profile.SecurityProfile.WithRuntimeCapability(planDirectory),
                () => PreparePlan(sessionId),
                (agentSessionId, messageId) => CompletePlan(sessionId, agentSessionId, messageId))
            : new MainAgentProfile(
                profile,
                () => profile.Prompt,
                static () => string.Empty,
                profile.SecurityProfile,
                static () => { },
                static (_, _) => null);
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
                            Action = new ChoiceAction { Mode = Default, Prompt = "Implement the approved plan." },
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
