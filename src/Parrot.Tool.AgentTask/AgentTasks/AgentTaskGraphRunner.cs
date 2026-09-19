using System.Text;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.AgentTasks;

internal sealed class AgentTaskGraphRunner(
    IModelRouter router,
    IAgentSessionScope ownerScope,
    AgentTurnSelection selection,
    IAgentTaskProgress progress,
    AgentTaskConfig configuration,
    HistoryForkBoundary rootHistoryBoundary)
{
    private const int MaxContextCharacters = 16 * 1024;
    private const int MaxSummaryCharacters = 16 * 1024;
    private const int MaxPromptCharacters = 256 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<CancellationTokenSource, TaskCompletionSource> _activeRoles = [];
    private bool _stopping;

    internal static string ResolveRoleProfile(string role) => role switch
    {
        "prepare" => "agent-task-prepare",
        "execute" => "agent-task-payload",
        "accept" => "agent-task-validation",
        _ => throw new ArgumentException($"Unknown AgentTask role: {role}", nameof(role)),
    };

    internal async Task<AgentTaskGraphResult> Run(
        AgentTaskArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var handles = progress.EnsureInitialized(artifact.Tasks, cancellationToken);
        try
        {
            var tasks = await RunSiblings(
                artifact.Tasks,
                handles,
                [],
                [],
                "task",
                ownerScope,
                cancellationToken).ConfigureAwait(false);
            var status = tasks.All(task => task.Status == AgentTaskExecutionStatus.Succeeded)
                ? AgentTaskExecutionStatus.Succeeded
                : tasks.Any(task => task.Status == AgentTaskExecutionStatus.Canceled)
                    ? AgentTaskExecutionStatus.Canceled
                    : AgentTaskExecutionStatus.Failed;
            return new AgentTaskGraphResult(status, tasks);
        }
        catch (OperationCanceledException)
        {
            BeginStopping();
            await StopRoles().ConfigureAwait(false);
            progress.MarkRemainingCanceled(CancellationToken.None);
            throw;
        }
        catch
        {
            BeginStopping();
            await StopRoles().ConfigureAwait(false);
            progress.MarkRemainingFailed(CancellationToken.None);
            throw;
        }
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<string>? RetainFeedback(List<string> feedback) =>
        feedback.Count == 0 ? null : feedback.AsReadOnly();

    private AgentTaskResult Blocked(string name, IReadOnlyList<string> dependencies) => new(
        name,
        AgentTaskExecutionStatus.Blocked,
        0,
        null,
        null,
        null,
        null,
        null,
        null,
        Render("agent-task.blocked-dependency", []),
        dependencies,
        null);

    private string BuildPreparePrompt(
        EffectiveAgentTask task,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskPrepareContext> contexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        string path)
    {
        var header = Header(task, siblings, ancestors, contexts, dependencies);
        return ComposePrompt(new StringBuilder(header), Render(
            "agent-task.prepare",
            ("header", string.Empty),
            ("path", path),
            ("declaration", Bound(AgentTaskPromptFormatter.Format(task), MaxSummaryCharacters))));
    }

    private string BuildPreparePromptWithResult(
        EffectiveAgentTask task,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskPrepareContext> contexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        string path,
        string result)
    {
        var prompt = new StringBuilder(Header(task, siblings, ancestors, contexts, dependencies));
        AppendResult(prompt, result);
        return ComposePrompt(prompt, Render(
            "agent-task.prepare",
            ("header", string.Empty),
            ("path", path),
            ("declaration", Bound(AgentTaskPromptFormatter.Format(task), MaxSummaryCharacters))));
    }

    private string BuildExecutionPrompt(
        EffectiveAgentTask task,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskPrepareContext> contexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        IReadOnlyList<string> feedback)
    {
        var prompt = new StringBuilder(Header(task, siblings, ancestors, contexts, dependencies));
        AppendFeedback(prompt, feedback);
        return ComposePrompt(prompt, Render(
            "agent-task.execution",
            ("header", string.Empty),
            ("feedback", string.Empty),
            ("instruction", task.Payload.Instruction ?? throw new InvalidOperationException("An instruction payload is required."))));
    }

    private string BuildLeafPrompt(
        EffectiveAgentTask task,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskPrepareContext> contexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        IReadOnlyList<string> feedback,
        string? result)
    {
        var prompt = new StringBuilder(Header(task, siblings, ancestors, contexts, dependencies));
        AppendResult(prompt, result);
        AppendFeedback(prompt, feedback);
        return ComposePrompt(prompt, Render(
            "agent-task.leaf",
            ("header", string.Empty),
            ("feedback", string.Empty),
            ("instruction", task.Payload.Instruction ?? throw new InvalidOperationException("An instruction payload is required."))));
    }

    private string BuildAcceptancePrompt(
        EffectiveAgentTask task,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskPrepareContext> contexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        IReadOnlyList<string> feedback,
        string execution,
        IReadOnlyList<AgentTaskResult>? nested,
        string? carriedResult)
    {
        var prompt = new StringBuilder(Header(task, siblings, ancestors, contexts, dependencies));
        AppendResult(prompt, carriedResult);
        AppendFeedback(prompt, feedback);
        var nestedText = nested is null
            ? string.Empty
            : string.Concat(
                "\n",
                Render(
                    "agent-task.nested-results",
                    ("results", Bound(AgentTaskGraphResult.SerializeNested(nested), MaxSummaryCharacters))));
        return ComposePrompt(prompt, Render(
            "agent-task.acceptance",
            ("header", string.Empty),
            ("feedback", string.Empty),
            ("execution", Bound(execution, MaxSummaryCharacters)),
            ("nested", nestedText)));
    }

    private string Render(string id, params (string Name, string Value)[] values) =>
        configuration.PromptTemplates.Render(id, [.. values.Select(value => new PromptTemplateArgument(value.Name, value.Value))]);

    private string Header(
        EffectiveAgentTask task,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskPrepareContext> contexts,
        IReadOnlyList<AgentTaskResult> dependencies)
    {
        var ancestorText = new StringBuilder();
        if (ancestors.Count > 0)
        {
            _ = ancestorText.Append(Render("agent-task.ancestor-header", []));
            foreach (var ancestor in ancestors)
            {
                _ = ancestorText.Append("\n[").Append(ancestor.Path).Append("] ")
                    .Append(Bound(ancestor.Description, MaxSummaryCharacters));
            }
        }

        var contextText = new StringBuilder();
        if (contexts.Count > 0)
        {
            _ = contextText.Append(Render("agent-task.context-header", []));
            foreach (var context in contexts)
            {
                _ = contextText.Append("\n[").Append(context.Path).Append("] ")
                    .Append(Bound(context.Context, MaxContextCharacters));
            }
        }

        var dependencyText = new StringBuilder();
        if (dependencies.Count > 0)
        {
            _ = dependencyText.Append(Render("agent-task.dependency-header", []));
            foreach (var dependency in dependencies)
            {
                _ = dependencyText.Append("\n[").Append(dependency.Name).Append("] ")
                    .Append(Bound(
                        dependency.Result
                        ?? dependency.Execution
                        ?? dependency.Verdict?.Evidence
                        ?? dependency.Failure
                        ?? dependency.Status.ToString(),
                        MaxSummaryCharacters));
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
            ("ancestors", ancestorText.ToString()),
            ("contexts", contextText.ToString()),
            ("dependencies", dependencyText.ToString()));
    }

    private void AppendResult(StringBuilder prompt, string? result)
    {
        if (result is not null)
        {
            _ = prompt.Append(Render("agent-task.result", ("result", Bound(result, MaxContextCharacters))));
        }
    }

    private void AppendFeedback(StringBuilder prompt, IReadOnlyList<string> feedback)
    {
        if (feedback.Count == 0)
        {
            return;
        }

        var items = new StringBuilder();
        foreach (var item in feedback)
        {
            _ = items.Append("\n- ").Append(Bound(item, MaxSummaryCharacters));
        }

        _ = prompt.Append(Render("agent-task.feedback", ("items", items.ToString())));
    }

    private AgentTaskPrepareResult ParsePreparation(string output, EffectiveAgentTask declared)
    {
        var preparation = AgentTaskParser.ParsePrepare(output);
        if (preparation.TaskPatch is not null)
        {
            AgentTaskParser.ValidateEffective(declared.Apply(preparation.TaskPatch));
        }

        return preparation with { Context = Bound(preparation.Context, MaxContextCharacters) };
    }

    private AgentTaskLeafResponse ParseLeafResponse(string output)
    {
        var response = AgentTaskParser.ParseLeafResponse(output);
        return response with { Result = Bound(response.Result, MaxContextCharacters) };
    }

    private string Bound(string text, int limit) => text.Length <= limit
        ? text
        : Render("agent-task.truncated", ("value", text.AsSpan(0, limit).ToString()));

    private string ComposePrompt(StringBuilder prefix, string suffix)
    {
        var available = MaxPromptCharacters - suffix.Length;
        return available <= 0
            ? suffix
            : string.Concat(Bound(prefix.ToString(), available), suffix);
    }

    private string RoleFailure(string role, AgentExecution result) =>
        Render(
            "agent-task.role-failure",
            ("role", role),
            ("status", result.Status.ToString().ToLowerInvariant()),
            ("error", result.Error));

    private async Task<IReadOnlyList<AgentTaskResult>> RunSiblings(
        IReadOnlyList<AgentTask> tasks,
        IReadOnlyList<AgentTaskNodeHandle> handles,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskPrepareContext> contexts,
        string parentPath,
        IAgentSessionScope owningAgentScope,
        CancellationToken cancellationToken)
    {
        if (tasks.Count != handles.Count)
        {
            throw new InvalidOperationException("Task progress handles do not match the effective task graph.");
        }

        var results = new AgentTaskResult?[tasks.Count];
        var indexes = tasks.Select((task, index) => (task.Name, index))
            .ToDictionary(item => item.Name, item => item.index, StringComparer.Ordinal);
        var running = new Dictionary<int, Task<AgentTaskResult>>();

        while (results.Any(result => result is null))
        {
            if (cancellationToken.IsCancellationRequested && running.Count > 0)
            {
                BeginStopping();
                await StopRoles().ConfigureAwait(false);
                try
                {
                    _ = await Task.WhenAll(running.Values).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var changed = false;

            for (var index = 0; index < tasks.Count; index++)
            {
                if (results[index] is not null || running.ContainsKey(index))
                {
                    continue;
                }

                var task = tasks[index];
                var dependencyResults = task.Dependencies
                    .Select(dependency => results[indexes[dependency]])
                    .ToArray();
                var failedDependencies = task.Dependencies
                    .Where(dependency => results[indexes[dependency]] is { Status: not AgentTaskExecutionStatus.Succeeded })
                    .ToArray();
                if (failedDependencies.Length > 0)
                {
                    results[index] = Blocked(task.Name, failedDependencies);
                    progress.MarkBlocked(handles[index], CancellationToken.None);
                    changed = true;
                    continue;
                }

                if (dependencyResults.Any(result => result is null))
                {
                    continue;
                }

                var dependencies = dependencyResults
                    .Select(result => result ?? throw new InvalidOperationException("A ready task has an unsettled dependency."))
                    .ToArray();
                progress.MarkRunning(handles[index], cancellationToken);
                running.Add(index, RunTask(
                    task,
                    handles[index],
                    [.. tasks.Where((_, siblingIndex) => siblingIndex != index)],
                    ancestors,
                    contexts,
                    dependencies,
                    $"{parentPath}/{task.Name}",
                    owningAgentScope,
                    cancellationToken));
                changed = true;
            }

            if (running.Count == 0)
            {
                if (!changed)
                {
                    throw new InvalidOperationException("The task graph scheduler made no progress.");
                }

                continue;
            }

            var completed = await Task.WhenAny(running.Values).ConfigureAwait(false);
            var completedPair = running.Single(pair => ReferenceEquals(pair.Value, completed));
            try
            {
                var result = await completed.ConfigureAwait(false);
                results[completedPair.Key] = result;
                progress.MarkTerminal(handles[completedPair.Key], result.Status, CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                BeginStopping();
                await StopRoles().ConfigureAwait(false);
                try
                {
                    _ = await Task.WhenAll(running.Values).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                throw;
            }
            catch (Exception failure)
            {
                results[completedPair.Key] = AgentTaskResult.CreateFailed(tasks[completedPair.Key].Name, failure.Message);
                progress.MarkTerminal(
                    handles[completedPair.Key],
                    AgentTaskExecutionStatus.Failed,
                    CancellationToken.None);
            }

            _ = running.Remove(completedPair.Key);
        }

        var completedResults = results.OfType<AgentTaskResult>().ToArray();
        if (completedResults.Length != results.Length)
        {
            throw new InvalidOperationException("The task graph contains an unsettled result.");
        }

        return Array.AsReadOnly(completedResults);
    }

    private async Task<AgentTaskResult> RunTask(
        AgentTask approved,
        AgentTaskNodeHandle handle,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskPrepareContext> inheritedContexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        string path,
        IAgentSessionScope owningAgentScope,
        CancellationToken cancellationToken)
    {
        var effective = EffectiveAgentTask.FromArtifact(approved);
        if (effective.Payload.Instruction is not null)
        {
            return await RunLeafTask(
                approved,
                effective,
                handle,
                siblings,
                ancestors,
                inheritedContexts,
                dependencies,
                path,
                owningAgentScope,
                cancellationToken).ConfigureAwait(false);
        }

        var childHandles = progress.GetChildren(handle);
        var declared = effective;
        var prepareRun = await RunRoleAndParse(
            effective.Model,
            "prepare",
            approved.Name,
            owningAgentScope,
            null,
            BuildPreparePrompt(effective, siblings, ancestors, inheritedContexts, dependencies, path),
            output => ParsePreparation(output, declared),
            cancellationToken).ConfigureAwait(false);
        var prepare = prepareRun.Execution;
        if (prepare.Status != AgentExecutionStatus.Succeeded)
        {
            return AgentTaskResult.CreateFailed(approved.Name, RoleFailure("prepare", prepare));
        }

        if (prepareRun.Value is not { } preparation)
        {
            return AgentTaskResult.CreateFailed(approved.Name, $"prepare response invalid: {prepareRun.Failure}");
        }

        if (preparation.TaskPatch is not null)
        {
            effective = effective.Apply(preparation.TaskPatch);
            childHandles = progress.UpdatePreparedTask(
                handle,
                effective.Description,
                preparation.TaskPatch.Payload,
                cancellationToken);
        }

        var currentContexts = inheritedContexts
            .Append(new AgentTaskPrepareContext(path, preparation.Context))
            .ToArray();
        var currentAncestors = ancestors
            .Append(new AgentTaskAncestor(path, effective.Description))
            .ToArray();
        return await RunLegacyAttempts(
            approved,
            effective,
            handle,
            childHandles,
            siblings,
            ancestors,
            currentAncestors,
            currentContexts,
            dependencies,
            path,
            preparation.TaskPatch,
            prepareRun.Scope,
            [],
            1,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentTaskResult> RunLeafTask(
        AgentTask approved,
        EffectiveAgentTask effective,
        AgentTaskNodeHandle handle,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskPrepareContext> inheritedContexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        string path,
        IAgentSessionScope owningAgentScope,
        CancellationToken cancellationToken)
    {
        var feedback = new List<string>();
        IAgentSessionScope? payloadAgentScope = null;
        string? currentResult = null;
        var maximumAttempts = configuration.MaximumAttempts;

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var promptContexts = inheritedContexts;
            var payloadRun = await RunRoleAndParse(
                effective.Model,
                "execute",
                approved.Name,
                owningAgentScope,
                payloadAgentScope,
                BuildLeafPrompt(effective, siblings, ancestors, promptContexts, dependencies, feedback, currentResult),
                ParseLeafResponse,
                cancellationToken).ConfigureAwait(false);
            payloadAgentScope = payloadRun.Scope;
            var executed = payloadRun.Execution;
            if (executed.Status != AgentExecutionStatus.Succeeded)
            {
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    null,
                    null,
                    currentResult,
                    null,
                    null,
                    RetainFeedback(feedback),
                    null,
                    RoleFailure("execution", executed));
            }

            if (payloadRun.Value is not { } response)
            {
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    null,
                    null,
                    currentResult,
                    null,
                    null,
                    RetainFeedback(feedback),
                    null,
                    $"leaf response invalid: {payloadRun.Failure}");
            }

            var verdict = response.Verdict with
            {
                Feedback = response.Verdict.Feedback is null
                    ? null
                    : Bound(response.Verdict.Feedback, MaxSummaryCharacters),
            };
            currentResult = response.Result;
            if (verdict.Kind == AcceptanceVerdictKind.Accept)
            {
                return new AgentTaskResult(
                    approved.Name,
                    AgentTaskExecutionStatus.Succeeded,
                    attempt,
                    null,
                    currentResult,
                    null,
                    null,
                    verdict,
                    RetainFeedback(feedback),
                    null,
                    null,
                    null);
            }

            if (verdict.Kind == AcceptanceVerdictKind.RejectAndHalt)
            {
                var rejection = verdict.Feedback
                    ?? throw new InvalidOperationException("A reject_and_halt verdict requires feedback.");
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    null,
                    null,
                    currentResult,
                    null,
                    verdict,
                    RetainFeedback(feedback),
                    null,
                    rejection);
            }

            var retryFeedback = verdict.Feedback
                ?? throw new InvalidOperationException("A reject_and_retry verdict requires feedback.");
            var replacementPayload = verdict.Payload
                ?? throw new InvalidOperationException("A reject_and_retry verdict requires a replacement payload.");
            currentResult = Bound(verdict.Context ?? response.Result, MaxContextCharacters);
            feedback.Add(retryFeedback);
            if (attempt == maximumAttempts)
            {
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    null,
                    null,
                    currentResult,
                    null,
                    verdict,
                    RetainFeedback(feedback),
                    null,
                    "acceptance requested reject_and_retry after the final attempt");
            }

            var replacement = effective with { Payload = replacementPayload };
            try
            {
                AgentTaskParser.ValidateEffective(replacement);
            }
            catch (ArgumentException failure)
            {
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    null,
                    null,
                    currentResult,
                    null,
                    verdict,
                    RetainFeedback(feedback),
                    null,
                    $"retry payload invalid: {failure.Message}");
            }

            effective = replacement;
            var childHandles = progress.ReplaceChildren(handle, effective.Payload, cancellationToken);
            progress.ReportRetry(path, attempt + 1, maximumAttempts, cancellationToken);
            if (effective.Payload.Tasks is null)
            {
                continue;
            }

            var retryContexts = inheritedContexts;
            var declared = effective;
            var prepareRun = await RunRoleAndParse(
                effective.Model,
                "prepare",
                approved.Name,
                owningAgentScope,
                null,
                BuildPreparePromptWithResult(effective, siblings, ancestors, retryContexts, dependencies, path, currentResult ?? throw new InvalidOperationException("A leaf retry requires a result.")),
                output => ParsePreparation(output, declared),
                cancellationToken).ConfigureAwait(false);
            var prepare = prepareRun.Execution;
            if (prepare.Status != AgentExecutionStatus.Succeeded)
            {
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    null,
                    null,
                    currentResult,
                    null,
                    verdict,
                    RetainFeedback(feedback),
                    null,
                    RoleFailure("prepare", prepare));
            }

            if (prepareRun.Value is not { } preparation)
            {
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    null,
                    null,
                    currentResult,
                    null,
                    verdict,
                    RetainFeedback(feedback),
                    null,
                    $"prepare response invalid: {prepareRun.Failure}");
            }

            if (preparation.TaskPatch is not null)
            {
                effective = effective.Apply(preparation.TaskPatch);
                childHandles = progress.UpdatePreparedTask(
                    handle,
                    effective.Description,
                    preparation.TaskPatch.Payload,
                    cancellationToken);
            }

            var currentContexts = inheritedContexts
                .Append(new AgentTaskPrepareContext(path, preparation.Context))
                .ToArray();
            var currentAncestors = ancestors
                .Append(new AgentTaskAncestor(path, effective.Description))
                .ToArray();
            return await RunLegacyAttempts(
                approved,
                effective,
                handle,
                childHandles,
                siblings,
                ancestors,
                currentAncestors,
                currentContexts,
                dependencies,
                path,
                preparation.TaskPatch,
                prepareRun.Scope,
                feedback,
                attempt + 1,
                currentResult,
                cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("The attempt loop terminated unexpectedly.");
    }

    private async Task<AgentTaskResult> RunLegacyAttempts(
        AgentTask approved,
        EffectiveAgentTask effective,
        AgentTaskNodeHandle handle,
        IReadOnlyList<AgentTaskNodeHandle> childHandles,
        IReadOnlyList<AgentTask> siblings,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskAncestor> currentAncestors,
        AgentTaskPrepareContext[] currentContexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        string path,
        AgentTaskPatch? taskPatch,
        IAgentSessionScope compositeAgentScope,
        List<string> feedback,
        int firstAttempt,
        string? carriedResult,
        CancellationToken cancellationToken)
    {
        IAgentSessionScope? executionAgentScope = null;
        string? execution = null;
        IReadOnlyList<AgentTaskResult>? nested = null;
        AcceptanceVerdict? verdict = null;
        var maximumAttempts = configuration.MaximumAttempts;

        for (var attempt = firstAttempt; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            execution = null;
            nested = null;
            verdict = null;
            if (effective.Payload.Instruction is not null)
            {
                var executionRun = await RunRole(
                    effective.Model,
                    "execute",
                    approved.Name,
                    compositeAgentScope,
                    executionAgentScope,
                    BuildExecutionPrompt(effective, siblings, ancestors, currentContexts, dependencies, feedback),
                    cancellationToken).ConfigureAwait(false);
                executionAgentScope = executionRun.Scope;
                var executed = executionRun.Execution;
                if (executed.Status != AgentExecutionStatus.Succeeded)
                {
                    return AgentTaskResult.CreateCompletedFailure(
                        approved.Name,
                        attempt,
                        taskPatch,
                        currentContexts[^1].Context,
                        null,
                        execution,
                        verdict,
                        RetainFeedback(feedback),
                        nested,
                        RoleFailure("execution", executed));
                }

                execution = Bound(executed.Output, MaxSummaryCharacters);
                nested = null;
            }
            else
            {
                executionAgentScope = null;
                var nestedTasks = effective.Payload.Tasks
                    ?? throw new InvalidOperationException("A composite payload requires nested tasks.");
                nested = await RunSiblings(
                    nestedTasks,
                    childHandles,
                    currentAncestors,
                    currentContexts,
                    path,
                    compositeAgentScope,
                    cancellationToken).ConfigureAwait(false);
                var succeeded = nested.Count(result => result.Status == AgentTaskExecutionStatus.Succeeded);
                execution = Render(
                    "agent-task.nested-summary",
                    ("succeeded", succeeded.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("total", nested.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            var acceptanceResult = carriedResult;
            var acceptanceRun = await RunRoleAndParse(
                effective.Model,
                "accept",
                approved.Name,
                compositeAgentScope,
                compositeAgentScope,
                BuildAcceptancePrompt(effective, siblings, ancestors, currentContexts, dependencies, feedback, execution, nested, acceptanceResult),
                AgentTaskParser.ParseVerdict,
                cancellationToken).ConfigureAwait(false);
            carriedResult = null;
            var reviewed = acceptanceRun.Execution;
            if (reviewed.Status != AgentExecutionStatus.Succeeded)
            {
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    taskPatch,
                    currentContexts[^1].Context,
                    nested is null ? execution : Bound(AgentTaskGraphResult.SerializeNested(nested), MaxSummaryCharacters),
                    execution,
                    verdict,
                    RetainFeedback(feedback),
                    nested,
                    RoleFailure("acceptance", reviewed));
            }

            if (acceptanceRun.Value is not { } reviewedVerdict)
            {
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    taskPatch,
                    currentContexts[^1].Context,
                    nested is null ? execution : Bound(AgentTaskGraphResult.SerializeNested(nested), MaxSummaryCharacters),
                    execution,
                    verdict,
                    RetainFeedback(feedback),
                    nested,
                    $"acceptance response invalid: {acceptanceRun.Failure}");
            }

            verdict = reviewedVerdict;

            if (verdict.Kind == AcceptanceVerdictKind.Accept)
            {
                return new AgentTaskResult(
                    approved.Name,
                    AgentTaskExecutionStatus.Succeeded,
                    attempt,
                    currentContexts[^1].Context,
                    nested is null ? execution : Bound(AgentTaskGraphResult.SerializeNested(nested), MaxSummaryCharacters),
                    taskPatch,
                    execution,
                    verdict,
                    RetainFeedback(feedback),
                    null,
                    null,
                    nested);
            }

            if (verdict.Kind == AcceptanceVerdictKind.RejectAndHalt)
            {
                var rejection = verdict.Feedback
                    ?? throw new InvalidOperationException("A reject_and_halt verdict requires feedback.");
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    taskPatch,
                    currentContexts[^1].Context,
                    nested is null ? execution : Bound(AgentTaskGraphResult.SerializeNested(nested), MaxSummaryCharacters),
                    execution,
                    verdict,
                    RetainFeedback(feedback),
                    nested,
                    rejection);
            }

            var retryFeedback = verdict.Feedback
                ?? throw new InvalidOperationException("A reject_and_retry verdict requires feedback.");
            var replacementPayload = verdict.Payload
                ?? throw new InvalidOperationException("A reject_and_retry verdict requires a replacement payload.");
            var replacementContext = verdict.Context is null
                ? currentContexts[^1].Context
                : Bound(verdict.Context, MaxContextCharacters);

            if (attempt == maximumAttempts)
            {
                feedback.Add(Bound(retryFeedback, MaxSummaryCharacters));
                currentContexts[^1] = new AgentTaskPrepareContext(path, replacementContext);
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    taskPatch,
                    currentContexts[^1].Context,
                    nested is null ? execution : Bound(AgentTaskGraphResult.SerializeNested(nested), MaxSummaryCharacters),
                    execution,
                    verdict,
                    RetainFeedback(feedback),
                    nested,
                    "acceptance requested reject_and_retry after the final attempt");
            }

            var replacement = effective with { Payload = replacementPayload };
            feedback.Add(Bound(retryFeedback, MaxSummaryCharacters));
            try
            {
                AgentTaskParser.ValidateEffective(replacement);
            }
            catch (ArgumentException failure)
            {
                return AgentTaskResult.CreateCompletedFailure(
                    approved.Name,
                    attempt,
                    taskPatch,
                    currentContexts[^1].Context,
                    nested is null ? execution : Bound(AgentTaskGraphResult.SerializeNested(nested), MaxSummaryCharacters),
                    execution,
                    verdict,
                    RetainFeedback(feedback),
                    nested,
                    $"retry payload invalid: {failure.Message}");
            }

            currentContexts[^1] = new AgentTaskPrepareContext(path, replacementContext);
            carriedResult = null;
            effective = replacement;
            childHandles = progress.ReplaceChildren(
                handle,
                effective.Payload,
                cancellationToken);
            progress.ReportRetry(path, attempt + 1, maximumAttempts, cancellationToken);
        }

        throw new InvalidOperationException("The attempt loop terminated unexpectedly.");
    }

    private async Task<ParsedRoleRun<T>> RunRoleAndParse<T>(
        string? requestedModel,
        string role,
        string taskName,
        IAgentSessionScope owningAgentScope,
        IAgentSessionScope? retainedAgentScope,
        string prompt,
        Func<string, T> parse,
        CancellationToken cancellationToken)
        where T : class
    {
        var run = await RunRole(requestedModel, role, taskName, owningAgentScope, retainedAgentScope, prompt, cancellationToken)
            .ConfigureAwait(false);
        var repairs = 0;
        while (true)
        {
            if (run.Execution.Status != AgentExecutionStatus.Succeeded)
            {
                return new ParsedRoleRun<T>(run.Scope, run.Execution, null, null);
            }

            try
            {
                return new ParsedRoleRun<T>(run.Scope, run.Execution, parse(run.Execution.Output), null);
            }
            catch (ArgumentException failure) when (repairs >= configuration.MaximumResponseRepairs)
            {
                return new ParsedRoleRun<T>(run.Scope, run.Execution, null, failure.Message);
            }
            catch (ArgumentException failure)
            {
                repairs++;
                run = await RunRole(
                    requestedModel,
                    role,
                    taskName,
                    owningAgentScope,
                    run.Scope,
                    Render("agent-task.response-repair", ("error", failure.Message)),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<AgentRoleRun> RunRole(
        string? requestedModel,
        string role,
        string taskName,
        IAgentSessionScope owningAgentScope,
        IAgentSessionScope? retainedAgentScope,
        string prompt,
        CancellationToken cancellationToken)
    {
        using var roleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IAgentSessionScope childScope;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (retainedAgentScope is null)
            {
                var requestedName = role is "execute" or "prepare" ? taskName : $"{taskName}-{role}";
                var historyBoundary = ReferenceEquals(owningAgentScope, ownerScope)
                    ? rootHistoryBoundary
                    : new HistoryForkBoundary.AfterCompletedHistory();
                childScope = owningAgentScope.AgentSpawner.GetOrSpawnScope(requestedName, () => new AgentLaunchRequest(
                    owningAgentScope.Session,
                    selection,
                    ResolveRoleProfile(role),
                    requestedModel is null ? selection.RequestedModel : router.Resolve(requestedModel).RequestedSelector,
                    requestedName,
                    Render("agent-task.child-scope", ("role", role), ("task_name", taskName)),
                    HistoryForkSelection.Parse(configuration.ForkParentHistory ? "full" : string.Empty),
                    historyBoundary,
                    AgentCompletionDeliveryPolicy.RetainedOnly));
            }
            else
            {
                childScope = retainedAgentScope;
            }

            _activeRoles.Add(roleCancellation, completion);
        }

        try
        {
            var output = await childScope.Session.SendAndWaitForResult(prompt, roleCancellation.Token).ConfigureAwait(false);
            return new AgentRoleRun(childScope, AgentExecution.Succeeded(output));
        }
        catch (AgentExecutionException exception)
        {
            var execution = exception.Status switch
            {
                AgentTaskStatus.Failed => AgentExecution.Failed(exception.Message),
                AgentTaskStatus.Canceled => AgentExecution.Canceled(),
                _ => throw new InvalidOperationException("agent execution exception did not represent a terminal failure", exception),
            };
            return new AgentRoleRun(childScope, execution);
        }
        finally
        {
            lock (_gate)
            {
                _ = _activeRoles.Remove(roleCancellation);
                completion.SetResult();
            }
        }
    }

    private async Task StopRoles()
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _activeRoles.SelectMany(role => new[] { role.Key.CancelAsync(), role.Value.Task })];
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private void BeginStopping()
    {
        lock (_gate)
        {
            _stopping = true;
        }
    }

    private sealed record AgentRoleRun(IAgentSessionScope Scope, AgentExecution Execution);

    private sealed record ParsedRoleRun<T>(IAgentSessionScope Scope, AgentExecution Execution, T? Value, string? Failure)
        where T : class;
}
