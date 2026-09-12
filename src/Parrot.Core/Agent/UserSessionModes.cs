using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionModes(ModeRegistry modes, IPromptTemplateCatalog promptTemplates)
{
    private readonly Lock _planGate = new();
    private readonly ModeRegistry _modes = modes ?? throw new ArgumentNullException(nameof(modes));
    private readonly IPromptTemplateCatalog _promptTemplates = promptTemplates
        ?? throw new ArgumentNullException(nameof(promptTemplates));

    private AgentScratchDirectory? _mainScratch;
    private string _planArtifact = string.Empty;
    private string _taskArtifact = string.Empty;

    internal UserSessionModes(ModeRegistry modes, IPromptTemplateCatalog promptTemplates, string planDirectory)
        : this(modes, promptTemplates) =>
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
                () => _promptTemplates.Render(
                    "mode.plan-workflow",
                    [
                        new("profile_prompt", profile.Prompt),
                        new("plan_artifact", GetPlanArtifact()),
                        new("task_artifact", GetTaskArtifact()),
                        new("plan_directory", GetPlanDirectory()),
                    ]),
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
                return ModeCompletionOutcome.Repair(_promptTemplates.Render(
                    "mode.plan-repair-read",
                    [
                        new("plan_artifact", artifact),
                        new("task_artifact", taskArtifact),
                        new("error", failure.Message),
                    ]));
            }
        }

        if (plan.Length == 0)
        {
            return ModeCompletionOutcome.Repair(_promptTemplates.Render(
                "mode.plan-repair-blank-plan",
                [new("plan_artifact", artifact)]));
        }

        if (taskJson.Length == 0)
        {
            return ModeCompletionOutcome.Repair(_promptTemplates.Render(
                "mode.plan-repair-blank-task",
                [new("task_artifact", taskArtifact)]));
        }

        AgentTaskArtifact tasks;
        try
        {
            tasks = AgentTaskParser.ParseArtifact(taskJson);
        }
        catch (ArgumentException failure)
        {
            return ModeCompletionOutcome.Repair(_promptTemplates.Render(
                "mode.plan-repair-invalid-task",
                [
                    new("task_artifact", taskArtifact),
                    new("error", failure.Message),
                ]));
        }

        var completion = new PlanCompleted
        {
            AgentSessionId = agentSessionId,
            MessageId = messageId,
            Markdown = plan,
            TaskTree = AgentTaskProgressSnapshot.FromPlannedTasks(tasks.Tasks),
            TaskDeclarations = { PlanTaskDeclaration.FromPlannedTasks(tasks.Tasks) },
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
                            Prompt = _promptTemplates.Render(
                                "mode.plan-implementation",
                                [
                                    new("plan_artifact", artifact),
                                    new("task_artifact", taskArtifact),
                                ]),
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
