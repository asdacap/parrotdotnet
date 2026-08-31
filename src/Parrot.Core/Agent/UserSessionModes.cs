using Parrot.AgentTasks;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionModes(ModeRegistry modes)
{
    private readonly Lock _planGate = new();
    private readonly ModeRegistry _modes = modes ?? throw new ArgumentNullException(nameof(modes));
    private AgentScratchDirectory? _mainScratch;
    private string _planArtifact = string.Empty;
    private string _taskArtifact = string.Empty;

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
            _taskArtifact = string.Empty;
        }
    }

    public IMode Resolve(string id)
    {
        var profile = _modes.Resolve(id);

        return string.Equals(profile.Id, ModeRegistry.Plan, StringComparison.Ordinal)
            ? new SessionMode(
                profile,
                () => $"{profile.Prompt} Write the canonical Markdown plan to this exact file: {GetPlanArtifact()}. Write the required AgentTask v1 JSON artifact to this exact file: {GetTaskArtifact()}. The JSON must be a strict object with schema_version equal to 1 and a nonempty tasks array; each task requires nonblank name, description, payload, and acceptance_criteria. Optional dependencies must be an array of distinct nonblank, case-sensitive names in the same sibling list; an optional model must be a nonblank string. Payload is either a nonblank string or a nonempty recursive task array. You may write optional supporting artifacts under this plan directory and reference them from the canonical plan: {GetPlanDirectory()}. Do not include the plan in your assistant response. Finish only after writing both canonical files.",
                profile.SecurityProfile,
                PreparePlan,
                CompletePlan)
            : new SessionMode(
                profile,
                () => profile.Prompt,
                profile.SecurityProfile,
                static () => { },
                static (_, _) => ModeCompletionOutcome.None);
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

    private string GetTaskArtifact()
    {
        lock (_planGate)
        {
            return _taskArtifact;
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
                SecureArtifact(_taskArtifact);
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
            var token = Guid.NewGuid().ToString("n");
            var artifact = Path.Combine(scratch.PlanDirectory, $"plan-{token}.md");
            var taskArtifact = Path.Combine(scratch.PlanDirectory, $"plan-{token}.tasks.json");

            try
            {
                using var stream = new FileStream(artifact, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var taskStream = new FileStream(
                    taskArtifact,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);
                SecureArtifact(artifact);
                SecureArtifact(taskArtifact);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ModeRegistryException)
            {
                try
                {
                    File.Delete(artifact);
                    File.Delete(taskArtifact);
                }
                catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
                {
                    throw new ModeRegistryException(
                        $"mode: create and secure correlated plan files: {failure.Message}; cleanup: {cleanupFailure.Message}");
                }

                throw new ModeRegistryException($"mode: create and secure correlated plan files: {failure.Message}");
            }

            _planArtifact = artifact;
            _taskArtifact = taskArtifact;
        }
    }

    private ModeCompletionOutcome CompletePlan(string agentSessionId, string messageId)
    {
        string artifact;
        string taskArtifact;
        string plan;
        string taskJson;

        lock (_planGate)
        {
            if (_planArtifact.Length == 0)
            {
                return ModeCompletionOutcome.None;
            }

            artifact = _planArtifact;
            taskArtifact = _taskArtifact;
            try
            {
                plan = File.ReadAllText(artifact).Trim();
                taskJson = File.ReadAllText(taskArtifact).Trim();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                var diagnostic =
                    $"Plan artifacts could not be read. Repair the exact Markdown path '{artifact}' and task JSON path '{taskArtifact}': {failure.Message}";
                return ModeCompletionOutcome.Repair(diagnostic);
            }
        }

        if (plan.Length == 0)
        {
            var diagnostic =
                $"The required Markdown plan artifact is blank. Write the complete plan to the exact path '{artifact}', then finish again.";
            return ModeCompletionOutcome.Repair(diagnostic);
        }

        if (taskJson.Length == 0)
        {
            var diagnostic =
                $"The required AgentTask JSON artifact is blank. Write valid schema_version 1 task JSON to the exact path '{taskArtifact}', then finish again.";
            return ModeCompletionOutcome.Repair(diagnostic);
        }

        try
        {
            _ = AgentTaskParser.ParseArtifact(taskJson);
        }
        catch (ArgumentException failure)
        {
            var diagnostic =
                $"The AgentTask JSON artifact at '{taskArtifact}' is invalid: {failure.Message} Repair that exact file, then finish again.";
            return ModeCompletionOutcome.Repair(diagnostic);
        }

        var completion = new PlanCompleted
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
                        Action = new ChoiceAction
                        {
                            Mode = ModeRegistry.Build,
                            Prompt = $"Implement the approved Markdown plan at {artifact}. Call run_agent_tasks with the approved task JSON path {taskArtifact}.",
                        },
                    },
                    new DialogChoice { Value = "no", Description = "Stop after planning", Aliases = { "n" } },
                },
                CustomChoice = "feedback",
                CustomDescription = "Provide feedback and revise the plan",
                CustomPrompt = "plan feedback: ",
                EmptyMessage = "enter yes, no, or feedback",
            },
        };

        return ModeCompletionOutcome.Completed(completion);
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
