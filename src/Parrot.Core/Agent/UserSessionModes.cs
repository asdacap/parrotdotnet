using Parrot.Protocol;

namespace Parrot.Agent;

internal sealed class UserSessionModes(ModeRegistry modes, string planDirectory)
{
    private readonly Lock _planGate = new();
    private string _planArtifact = string.Empty;

    public ProfileRegistry Profiles => modes.Profiles;

    public MainAgentProfile Resolve(string id)
    {
        var profile = modes.Resolve(id);

        return string.Equals(profile.Id, ModeRegistry.Plan, StringComparison.Ordinal)
            ? new MainAgentProfile(
                profile,
                () => $"{profile.Prompt} to this exact file: {PlanArtifact()}. You may write optional supporting artifacts under this plan directory and reference them from the canonical plan: {planDirectory}. Do not include the plan in your assistant response. Finish only after writing the canonical file.",
                PlanArtifact,
                profile.SecurityProfile.WithRuntimeCapability(planDirectory),
                PreparePlan,
                CompletePlan)
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

    private string PlanArtifact()
    {
        lock (_planGate)
        {
            return _planArtifact;
        }
    }

    private void PreparePlan()
    {
        lock (_planGate)
        {
            if (_planArtifact.Length > 0)
            {
                SecureArtifact(_planArtifact);
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
            _planArtifact = artifact;
        }
    }

    private PlanCompleted? CompletePlan(string agentSessionId, string messageId)
    {
        string plan;

        lock (_planGate)
        {
            if (_planArtifact.Length == 0)
            {
                return null;
            }

            try
            {
                plan = File.ReadAllText(_planArtifact).Trim();
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
                AgentSessionId = agentSessionId,
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
                            Action = new ChoiceAction { Mode = modes.Default, Prompt = "Implement the approved plan." },
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
