using System.Text;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Store;

namespace Parrot.AgentTasks;

/// <summary>Runs one task on its retained sub-agent, retrying a rejected attempt up to the configured attempt limit.</summary>
internal sealed class AgentTaskRunner(AgentTaskConfig configuration)
{
    private const int MaxContextCharacters = 16 * 1024;
    private const int MaxSummaryCharacters = 16 * 1024;
    private const int MaxPromptCharacters = 256 * 1024;

    /// <summary>
    /// Returns the task in its Succeeded or Failed state. A composite task first upserts its changed children into the
    /// sub-agent's own task service, which the sub-agent then owns. Cancellation stops the sub-agent's turn and throws.
    /// </summary>
    internal async Task<AgentTask> Run(
        AgentTask task,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTask> dependencies,
        IAgentSessionScope scope,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        if (task.Payload.Tasks is { } children)
        {
            var childTasks = scope.GetService<IAgentTaskService>();
            var declared = childTasks.Snapshot();
            var changed = children.Where(child => !declared.Any(current => current.HasSameDefinition(child))).ToArray();
            if (changed.Length > 0)
            {
                try
                {
                    childTasks.SetTasks(changed, selection, new HistoryForkBoundary.AfterCompletedHistory());
                }
                catch (ArgumentException failure)
                {
                    return task with { State = AgentTaskExecutionStatus.Failed, Result = null, Failure = failure.Message };
                }
            }

            childTasks.ApplyVisibilityChanges(declared, children);
        }

        var feedback = new List<string>();
        string? carriedResult = null;
        var current = task;
        for (var attempt = 1; ; attempt++)
        {
            var (response, failure) = await RunAndParse(
                scope,
                BuildPrompt(current, siblings, dependencies, feedback, carriedResult),
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return task with { State = AgentTaskExecutionStatus.Failed, Result = carriedResult, Failure = failure };
            }

            var verdict = response.Verdict;
            var result = Bound(response.Result, MaxContextCharacters);
            switch (verdict.Kind)
            {
                case AcceptanceVerdictKind.Accept:
                    return task with { State = AgentTaskExecutionStatus.Succeeded, Result = result, Failure = null };
                case AcceptanceVerdictKind.RejectAndHalt:
                    return task with
                    {
                        State = AgentTaskExecutionStatus.Failed,
                        Result = result,
                        Failure = Bound(verdict.Feedback ?? string.Empty, MaxSummaryCharacters),
                    };
                case AcceptanceVerdictKind.RejectAndRetry:
                default:
                    break;
            }

            var retryFeedback = Bound(verdict.Feedback ?? string.Empty, MaxSummaryCharacters);
            carriedResult = Bound(verdict.ReplacementResult ?? response.Result, MaxContextCharacters);
            if (attempt >= configuration.MaximumAttempts)
            {
                return task with
                {
                    State = AgentTaskExecutionStatus.Failed,
                    Result = carriedResult,
                    Failure = Render(
                        "agent-task.attempts-exhausted",
                        ("attempts", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ("feedback", retryFeedback)),
                };
            }

            feedback.Add(retryFeedback);
            if (verdict.ReplacementInstruction is { } instruction && current.Payload.Instruction is not null)
            {
                current = current with { Payload = AgentTaskPayload.FromInstruction(instruction) };
            }
        }
    }

    private async Task<(AgentTaskLeafResponse? Response, string? Failure)> RunAndParse(
        IAgentSessionScope scope,
        string prompt,
        CancellationToken cancellationToken)
    {
        var repairs = 0;
        while (true)
        {
            string output;
            try
            {
                output = await scope.Session.SendAndWaitForResult(prompt, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentExecutionException exception)
            {
                return (null, Render(
                    "agent-task.role-failure",
                    ("role", "execute"),
                    ("status", exception.Status.ToString().ToLowerInvariant()),
                    ("error", exception.Message)));
            }

            try
            {
                return (AgentTaskParser.ParseLeafResponse(output), null);
            }
            catch (ArgumentException failure) when (repairs < configuration.MaximumResponseRepairs)
            {
                repairs++;
                prompt = Render("agent-task.response-repair", ("error", failure.Message));
            }
            catch (ArgumentException failure)
            {
                return (null, $"leaf response invalid: {failure.Message}");
            }
        }
    }

    private string BuildPrompt(
        AgentTask task,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTask> dependencies,
        List<string> feedback,
        string? carriedResult)
    {
        var prompt = new StringBuilder(Header(task, siblings, dependencies));
        if (carriedResult is not null)
        {
            _ = prompt.Append(Render("agent-task.result", ("result", carriedResult)));
        }

        if (feedback.Count > 0)
        {
            var items = new StringBuilder();
            foreach (var item in feedback)
            {
                _ = items.Append("\n- ").Append(item);
            }

            _ = prompt.Append(Render("agent-task.feedback", ("items", items.ToString())));
        }

        var children = string.Concat((task.Payload.Tasks ?? []).Select(child => Render(
            "agent-task.composite-child",
            ("name", child.Name),
            ("description", child.Description))));
        var suffix = task.Payload.Instruction is { } instruction
            ? Render("agent-task.leaf", ("header", string.Empty), ("feedback", string.Empty), ("instruction", instruction))
            : Render("agent-task.composite", ("header", string.Empty), ("feedback", string.Empty), ("children", children));
        var available = MaxPromptCharacters - suffix.Length;
        return available <= 0
            ? suffix
            : string.Concat(Bound(prompt.ToString(), available), suffix);
    }

    private string Header(AgentTask task, IReadOnlyList<AgentTask> siblings, IReadOnlyList<AgentTask> dependencies)
    {
        var dependencyText = new StringBuilder();
        if (dependencies.Count > 0)
        {
            _ = dependencyText.Append(Render("agent-task.dependency-header", []));
            foreach (var dependency in dependencies)
            {
                _ = dependencyText.Append(Render(
                    "agent-task.dependency-item",
                    ("name", dependency.Name),
                    ("description", dependency.Description),
                    ("result", Bound(dependency.Result ?? string.Empty, MaxSummaryCharacters))));
            }
        }

        var siblingText = new StringBuilder();
        foreach (var sibling in siblings)
        {
            _ = siblingText.Append(Render(
                "agent-task.sibling-item",
                ("name", sibling.Name),
                ("description", Bound(sibling.Description, MaxSummaryCharacters))));
        }

        var siblingScope = siblings.Count == 0
            ? string.Empty
            : Render("agent-task.sibling-scope", ("items", siblingText.ToString()));
        return siblingScope + Render(
            "agent-task.header",
            ("task_name", task.Name),
            ("description", task.Description),
            ("acceptance_criteria", task.AcceptanceCriteria),
            ("dependencies", dependencyText.ToString()));
    }

    private string Render(string id, params (string Name, string Value)[] values) =>
        configuration.PromptTemplates.Render(id, [.. values.Select(value => new PromptTemplateArgument(value.Name, value.Value))]);

    private string Bound(string text, int limit) => text.Length <= limit
        ? text
        : Render("agent-task.truncated", ("value", text.AsSpan(0, limit).ToString()));
}
