using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionModes(ModeRegistry modes)
{
    private readonly Lock _planGate = new();
    private readonly ModeRegistry _modes = modes ?? throw new ArgumentNullException(nameof(modes));
    private AgentScratchDirectory? _mainScratch;
    private string _planArtifact = string.Empty;

    internal UserSessionModes(ModeRegistry modes, string planDirectory)
        : this(modes) =>
        _mainScratch = new AgentScratchDirectory(
            Path.GetDirectoryName(planDirectory)
            ?? throw new ArgumentException("A plan directory must have a parent.", nameof(planDirectory)));

    public void Attach(AgentScratchDirectory mainScratch)
    {
        ArgumentNullException.ThrowIfNull(mainScratch);
        lock (_planGate)
        {
            _mainScratch = mainScratch;
            _planArtifact = string.Empty;
        }
    }

    public IMode Resolve(string id)
    {
        var profile = _modes.Resolve(id);

        return string.Equals(profile.Id, ModeRegistry.Plan, StringComparison.Ordinal)
            ? new SessionMode(
                profile,
                () => $"{profile.Prompt} to this exact file: {GetPlanArtifact()}. You may write optional supporting artifacts under this plan directory and reference them from the canonical plan: {GetPlanDirectory()}. Do not include the plan in your assistant response. Finish only after writing the canonical file.",
                profile.SecurityProfile,
                PreparePlan,
                CompletePlan)
            : new SessionMode(
                profile,
                () => profile.Prompt,
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

    private string GetPlanArtifact()
    {
        lock (_planGate)
        {
            return _planArtifact;
        }
    }

    private string GetPlanDirectory() => RequireMainScratch().PlanDirectory;

    private void PreparePlan()
    {
        lock (_planGate)
        {
            if (_planArtifact.Length > 0)
            {
                SecureArtifact(_planArtifact);
                return;
            }

            var scratch = RequireMainScratch();
            try
            {
                scratch.ProvisionPlanDirectory();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new ModeRegistryException($"mode: create plan directory: {failure.Message}");
            }

            SecureDirectory();
            var artifact = Path.Combine(scratch.PlanDirectory, $"plan-{Guid.NewGuid():n}.md");

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
                            Action = new ChoiceAction { Mode = _modes.Default, Prompt = "Implement the approved plan." },
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

    private AgentScratchDirectory RequireMainScratch() =>
        _mainScratch ?? throw new ModeRegistryException("the main agent scratch directory is not attached");

    private void SecureDirectory()
    {
        var planDirectory = RequireMainScratch().PlanDirectory;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(
                planDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
