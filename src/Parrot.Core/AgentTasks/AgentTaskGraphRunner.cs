using System.Text;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.AgentTasks;

internal sealed class AgentTaskGraphRunner(
    AgentRegistry agents,
    ModelRouter router,
    AgentSession owner,
    AgentTurnSelection selection,
    AgentTaskProgress progress,
    AgentTaskConfig configuration)
{
    private const int MaxContextCharacters = 16 * 1024;
    private const int MaxSummaryCharacters = 16 * 1024;
    private const int MaxPromptCharacters = 256 * 1024;
    private readonly Lock _gate = new();
    private readonly HashSet<AgentSession> _activeChildren = [];
    private bool _stopping;
    private int _launchSequence;

    internal async Task<AgentTaskGraphResult> Run(
        AgentTaskArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var handles = progress.Initialize(artifact.Tasks, cancellationToken);
        try
        {
            var tasks = await RunSiblings(
                artifact.Tasks,
                handles,
                [],
                [],
                "task",
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
            await StopChildren().ConfigureAwait(false);
            progress.MarkRemainingCanceled(CancellationToken.None);
            throw;
        }
        catch
        {
            BeginStopping();
            await StopChildren().ConfigureAwait(false);
            throw;
        }
    }

    private static string BuildResearchPrompt(
        EffectiveAgentTask task,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskResearchContext> contexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        string path)
    {
        var prefix = Header("research pre-hook", task, ancestors, contexts, dependencies);
        var suffix = new StringBuilder("\nCurrent task path: ").Append(path)
            .Append("\nOriginal current task declaration:\n")
            .Append(Bound(AgentTaskPromptFormatter.Format(task), MaxSummaryCharacters))
            .Append("\nResearch and prepare this task. Return only strict JSON with no prose or code fence: ")
            .Append("{\"context\":\"nonblank findings\",\"task_patch\":{\"description\":\"optional\",\"payload\":\"optional\",\"acceptance_criteria\":\"optional\",\"model\":\"optional\"}}. ")
            .Append("Omit task_patch when no change is needed; when present omit every unchanged field.")
            .ToString();
        return ComposePrompt(prefix, suffix);
    }

    private static string BuildExecutionPrompt(
        EffectiveAgentTask task,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskResearchContext> contexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        IReadOnlyList<string> feedback)
    {
        var prefix = Header("payload executor", task, ancestors, contexts, dependencies);
        AppendFeedback(prefix, feedback);
        var suffix = string.Concat(
            "\nExecute this instruction and return the execution result:\n",
            task.Payload.Instruction);
        return ComposePrompt(prefix, suffix);
    }

    private static string BuildAcceptancePrompt(
        EffectiveAgentTask task,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskResearchContext> contexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        IReadOnlyList<string> feedback,
        string execution,
        IReadOnlyList<AgentTaskResult>? nested)
    {
        var prefix = Header("acceptance reviewer", task, ancestors, contexts, dependencies);
        AppendFeedback(prefix, feedback);
        var suffix = new StringBuilder("\nExecution result:\n").Append(Bound(execution, MaxSummaryCharacters));
        if (nested is not null)
        {
            _ = suffix.Append("\nNested task results (structured JSON):\n")
                .Append(Bound(AgentTaskGraphResult.SerializeNested(nested), MaxSummaryCharacters));
        }

        _ = suffix.Append("\nAssess the completed attempt. Return only one strict JSON object with no prose or code fence: ")
            .Append("{\"verdict\":\"accept\",\"evidence\":\"nonblank\"}, ")
            .Append("{\"verdict\":\"reject_and_halt\",\"feedback\":\"nonblank\"}, or ")
            .Append("{\"verdict\":\"reject_and_retry\",\"feedback\":\"nonblank\",\"payload\":\"replacement instruction or task array\",\"context\":\"optional nonblank replacement research context\"}. ")
            .Append("A reject_and_retry context replaces this task's research context for later attempts and descendants; omit it to retain the existing context.");
        return ComposePrompt(prefix, suffix.ToString());
    }

    private static StringBuilder Header(
        string role,
        EffectiveAgentTask task,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskResearchContext> contexts,
        IReadOnlyList<AgentTaskResult> dependencies)
    {
        var prompt = new StringBuilder("AgentTask role: ").Append(role)
            .Append("\nTask: ").Append(task.Name)
            .Append("\nDescription: ").Append(task.Description)
            .Append("\nAcceptance criteria: ").Append(task.AcceptanceCriteria);
        if (ancestors.Count > 0)
        {
            _ = prompt.Append("\nAncestor tasks (root to parent):");
            foreach (var ancestor in ancestors)
            {
                _ = prompt.Append("\n[").Append(ancestor.Path).Append("] ")
                    .Append(Bound(ancestor.Description, MaxSummaryCharacters));
            }
        }

        if (contexts.Count > 0)
        {
            _ = prompt.Append("\nResearch context (root to current):");
            foreach (var context in contexts)
            {
                _ = prompt.Append("\n[").Append(context.Path).Append("] ")
                    .Append(Bound(context.Context, MaxContextCharacters));
            }
        }

        if (dependencies.Count > 0)
        {
            _ = prompt.Append("\nDirect dependency summaries:");
            foreach (var dependency in dependencies)
            {
                _ = prompt.Append("\n[").Append(dependency.Name).Append("] ")
                    .Append(Bound(dependency.Execution ?? dependency.Failure ?? dependency.Status.ToString(), MaxSummaryCharacters));
            }
        }

        return prompt;
    }

    private static void AppendFeedback(StringBuilder prompt, IReadOnlyList<string> feedback)
    {
        if (feedback.Count == 0)
        {
            return;
        }

        _ = prompt.Append("\nRetry feedback:");
        foreach (var item in feedback)
        {
            _ = prompt.Append("\n- ").Append(Bound(item, MaxSummaryCharacters));
        }
    }

    private static string Bound(string text, int limit) => text.Length <= limit
        ? text
        : string.Concat(text.AsSpan(0, limit), "\n[truncated]");

    private static string ComposePrompt(StringBuilder prefix, string suffix)
    {
        var available = MaxPromptCharacters - suffix.Length;
        return available <= 0
            ? suffix
            : string.Concat(Bound(prefix.ToString(), available), suffix);
    }

    private static string RoleFailure(string role, AgentExecution result) =>
        $"{role} agent {result.Status.ToString().ToLowerInvariant()}: {result.Error}";

    private static AgentTaskResult Failed(string name, string failure) => new(
        name,
        AgentTaskExecutionStatus.Failed,
        0,
        null,
        null,
        null,
        null,
        null,
        failure,
        null,
        null);

    private static AgentTaskResult Blocked(string name, IReadOnlyList<string> dependencies) => new(
        name,
        AgentTaskExecutionStatus.Blocked,
        0,
        null,
        null,
        null,
        null,
        null,
        "dependency did not succeed",
        dependencies,
        null);

    private static System.Collections.ObjectModel.ReadOnlyCollection<string>? RetainFeedback(List<string> feedback) =>
        feedback.Count == 0 ? null : feedback.AsReadOnly();

    private static AgentTaskResult CompletedFailure(
        string name,
        int attempt,
        ResearchHookResult hook,
        string context,
        string? execution,
        AcceptanceVerdict? verdict,
        IReadOnlyList<string>? retryFeedback,
        IReadOnlyList<AgentTaskResult>? nested,
        string failure) => new(
            name,
            AgentTaskExecutionStatus.Failed,
            attempt,
            context,
            hook.TaskPatch,
            execution,
            verdict,
            retryFeedback,
            failure,
            null,
            nested);

    private async Task<IReadOnlyList<AgentTaskResult>> RunSiblings(
        IReadOnlyList<AgentTask> tasks,
        IReadOnlyList<AgentTaskProgress.NodeHandle> handles,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskResearchContext> contexts,
        string parentPath,
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
                await StopChildren().ConfigureAwait(false);
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
                    ancestors,
                    contexts,
                    dependencies,
                    $"{parentPath}/{task.Name}",
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
                await StopChildren().ConfigureAwait(false);
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
                results[completedPair.Key] = Failed(tasks[completedPair.Key].Name, failure.Message);
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
        AgentTaskProgress.NodeHandle handle,
        IReadOnlyList<AgentTaskAncestor> ancestors,
        IReadOnlyList<AgentTaskResearchContext> inheritedContexts,
        IReadOnlyList<AgentTaskResult> dependencies,
        string path,
        CancellationToken cancellationToken)
    {
        var effective = EffectiveAgentTask.FromArtifact(approved);
        var childHandles = progress.GetChildren(handle);
        var researchRun = await RunRole(
            effective.Model,
            "research",
            approved.Name,
            null,
            BuildResearchPrompt(effective, ancestors, inheritedContexts, dependencies, path),
            cancellationToken).ConfigureAwait(false);
        var research = researchRun.Execution;
        if (research.Status != AgentExecutionStatus.Succeeded)
        {
            return Failed(approved.Name, RoleFailure("research", research));
        }

        ResearchHookResult hook;
        try
        {
            hook = AgentTaskParser.ParseResearchHook(research.Output);
            hook = hook with { Context = Bound(hook.Context, MaxContextCharacters) };
            if (hook.TaskPatch is not null)
            {
                var patched = effective.Apply(hook.TaskPatch);
                AgentTaskParser.ValidateEffective(patched);
                if (patched.Model is not null)
                {
                    _ = router.Resolve(patched.Model);
                }

                effective = patched;
                if (hook.TaskPatch.Payload is not null)
                {
                    childHandles = progress.ReplaceChildren(
                        handle,
                        effective.Payload,
                        cancellationToken);
                }
            }
        }
        catch (Exception failure) when (failure is ArgumentException or LLMProviderException)
        {
            return Failed(approved.Name, $"research response invalid: {failure.Message}");
        }

        var currentContexts = inheritedContexts
            .Append(new AgentTaskResearchContext(path, Bound(hook.Context, MaxContextCharacters)))
            .ToArray();
        var currentAncestors = ancestors
            .Append(new AgentTaskAncestor(path, effective.Description))
            .ToArray();
        var feedback = new List<string>();
        AgentSession? executionAgent = null;
        AgentSession? acceptanceAgent = null;
        string? execution = null;
        IReadOnlyList<AgentTaskResult>? nested = null;
        AcceptanceVerdict? verdict = null;
        var maximumAttempts = configuration.MaximumAttempts;

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
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
                    executionAgent,
                    BuildExecutionPrompt(effective, ancestors, currentContexts, dependencies, feedback),
                    cancellationToken).ConfigureAwait(false);
                executionAgent = executionRun.Agent;
                var executed = executionRun.Execution;
                if (executed.Status != AgentExecutionStatus.Succeeded)
                {
                    return CompletedFailure(
                        approved.Name,
                        attempt,
                        hook,
                        currentContexts[^1].Context,
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
                executionAgent = null;
                var nestedTasks = effective.Payload.Tasks
                    ?? throw new InvalidOperationException("A composite payload requires nested tasks.");
                nested = await RunSiblings(
                    nestedTasks,
                    childHandles,
                    currentAncestors,
                    currentContexts,
                    path,
                    cancellationToken).ConfigureAwait(false);
                var succeeded = nested.Count(result => result.Status == AgentTaskExecutionStatus.Succeeded);
                execution = $"nested task graph: {succeeded}/{nested.Count} tasks succeeded";
            }

            var acceptanceRun = await RunRole(
                effective.Model,
                "accept",
                approved.Name,
                acceptanceAgent,
                BuildAcceptancePrompt(effective, ancestors, currentContexts, dependencies, feedback, execution, nested),
                cancellationToken).ConfigureAwait(false);
            acceptanceAgent = acceptanceRun.Agent;
            var reviewed = acceptanceRun.Execution;
            if (reviewed.Status != AgentExecutionStatus.Succeeded)
            {
                return CompletedFailure(
                    approved.Name,
                    attempt,
                    hook,
                    currentContexts[^1].Context,
                    execution,
                    verdict,
                    RetainFeedback(feedback),
                    nested,
                    RoleFailure("acceptance", reviewed));
            }

            try
            {
                verdict = AgentTaskParser.ParseVerdict(reviewed.Output);
            }
            catch (ArgumentException failure)
            {
                return CompletedFailure(
                    approved.Name,
                    attempt,
                    hook,
                    currentContexts[^1].Context,
                    execution,
                    verdict,
                    RetainFeedback(feedback),
                    nested,
                    $"acceptance response invalid: {failure.Message}");
            }

            if (verdict.Kind == AcceptanceVerdictKind.Accept)
            {
                return new AgentTaskResult(
                    approved.Name,
                    AgentTaskExecutionStatus.Succeeded,
                    attempt,
                    currentContexts[^1].Context,
                    hook.TaskPatch,
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
                return CompletedFailure(
                    approved.Name,
                    attempt,
                    hook,
                    currentContexts[^1].Context,
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
                currentContexts[^1] = new AgentTaskResearchContext(path, replacementContext);
                return CompletedFailure(
                    approved.Name,
                    attempt,
                    hook,
                    currentContexts[^1].Context,
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
                return CompletedFailure(
                    approved.Name,
                    attempt,
                    hook,
                    currentContexts[^1].Context,
                    execution,
                    verdict,
                    RetainFeedback(feedback),
                    nested,
                    $"retry payload invalid: {failure.Message}");
            }

            currentContexts[^1] = new AgentTaskResearchContext(path, replacementContext);
            effective = replacement;
            childHandles = progress.ReplaceChildren(
                handle,
                effective.Payload,
                cancellationToken);
        }

        throw new InvalidOperationException("The attempt loop terminated unexpectedly.");
    }

    private async Task<AgentRoleRun> RunRole(
        string? requestedModel,
        string role,
        string taskName,
        AgentSession? retainedAgent,
        string prompt,
        CancellationToken cancellationToken)
    {
        var model = requestedModel is null
            ? selection.RequestedModel
            : router.Resolve(requestedModel).RequestedSelector;
        AgentSession child;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (retainedAgent is null)
            {
                var sequence = Interlocked.Increment(ref _launchSequence);
                child = agents.Spawn(new AgentLaunchRequest(
                    owner,
                    selection,
                    "worker",
                    model,
                    $"task-{role}-{taskName}-{sequence}",
                    $"AgentTask {role} for {taskName}",
                    HistoryForkSelection.Parse(string.Empty),
                    0,
                    string.Empty,
                    AgentCompletionDeliveryPolicy.RetainedOnly));
            }
            else
            {
                child = retainedAgent;
            }

            _ = _activeChildren.Add(child);
        }

        try
        {
            _ = await child.Send(prompt, cancellationToken).ConfigureAwait(false);
            var waited = await child.Wait(0, cancellationToken).ConfigureAwait(false);
            var execution = waited.Status switch
            {
                AgentTaskStatus.Succeeded => AgentExecution.Succeeded(waited.Output),
                AgentTaskStatus.Canceled => AgentExecution.Canceled(),
                _ => AgentExecution.Failed(waited.Error),
            };
            return new AgentRoleRun(child, execution);
        }
        catch (OperationCanceledException)
        {
            await child.Abort(CancellationToken.None).ConfigureAwait(false);
            _ = await child.Wait(0, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            Untrack(child);
        }
    }

    private async Task StopChildren()
    {
        AgentSession[] active;
        lock (_gate)
        {
            active = [.. _activeChildren];
        }

        await Task.WhenAll(active.Select(async child =>
        {
            await child.Abort(CancellationToken.None).ConfigureAwait(false);
            _ = await child.Wait(0, CancellationToken.None).ConfigureAwait(false);
        })).ConfigureAwait(false);
    }

    private void BeginStopping()
    {
        lock (_gate)
        {
            _stopping = true;
        }
    }

    private void Untrack(AgentSession child)
    {
        lock (_gate)
        {
            _ = _activeChildren.Remove(child);
        }
    }

    private sealed record AgentRoleRun(AgentSession Agent, AgentExecution Execution);
}
