using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class PlanModeWorkflow(AgentScratchDirectory mainScratch, IPromptTemplateCatalog promptTemplates) : IModeWorkflow
{
    private readonly Lock _planGate = new();

    private string _planArtifact = string.Empty;
    private string _taskArtifact = string.Empty;

    public string RenderPrompt(IAgentProfile profile) =>
        promptTemplates.Render(
            "mode.plan-workflow",
            [
                new("profile_prompt", profile.Prompt),
                new("plan_artifact", GetPlanArtifact()),
                new("task_artifact", GetTaskArtifact()),
                new("plan_directory", mainScratch.PlanDirectory),
            ]);

    public void Prepare()
    {
        lock (_planGate)
        {
            if (_planArtifact.Length > 0)
            {
                SecureArtifact(_planArtifact);
                SecureArtifact(_taskArtifact);
                return;
            }

            try
            {
                mainScratch.ProvisionPlanDirectory();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new ModeRegistryException($"mode: create plan directory: {failure.Message}");
            }

            SecureDirectory();
            var token = Guid.NewGuid().ToString("n");
            var artifact = Path.Combine(mainScratch.PlanDirectory, $"plan-{token}.md");
            var taskArtifact = Path.Combine(mainScratch.PlanDirectory, $"plan-{token}.tasks.json");

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

    public ModeCompletionOutcome Complete()
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
                return ModeCompletionOutcome.Repair(promptTemplates.Render(
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
            return ModeCompletionOutcome.Repair(promptTemplates.Render(
                "mode.plan-repair-blank-plan",
                [new("plan_artifact", artifact)]));
        }

        if (taskJson.Length == 0)
        {
            return ModeCompletionOutcome.Repair(promptTemplates.Render(
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
            return ModeCompletionOutcome.Repair(promptTemplates.Render(
                "mode.plan-repair-invalid-task",
                [
                    new("task_artifact", taskArtifact),
                    new("error", failure.Message),
                ]));
        }

        var completion = new PlanCompleted
        {
            Markdown = plan,
            TaskTree = tasks.CaptureProgress(),
            TaskDeclarations = { tasks.DeclareTasks() },
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
                            Prompt = promptTemplates.Render(
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

    private void SecureDirectory()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(
                mainScratch.PlanDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
