using System.Diagnostics;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Agent;

// Every call the model made gets a result, even when the turn is stopped
// part-way through: a provider rejects a history holding a call with no
// answer, so an interrupt that left one behind would break every later
// prompt rather than only this turn (principle 6).
internal sealed class ToolBatchSettlement(
    AgentIdentity identity,
    IAgentHistory history,
    AgentConversation conversation,
    IEventBroker eventBroker,
    AgentEventEmitter eventEmitter,
    AgentUsageRecorder usage,
    ToolOutputBlobStore toolOutputBlobs,
    IPromptTemplateCatalog promptTemplates,
    RequestLimitsConfig requestLimits,
    AgentSessionActivity activity,
    IDiagnosticLog diagnostics)
{
    private readonly string _interruptedResult = promptTemplates.Render("agent-session.interrupted-result", []);
    private long _settledConversationSequence;

    // Each invocation captures its security profile when it starts, so a
    // permission granted earlier in the batch applies to the calls after it.
    public async Task Reconcile(
        AgentTurnSelection selection,
        ToolSnapshot snapshot,
        Func<SecurityProfile> captureSecurity,
        CancellationToken cancellationToken)
    {
        // Everything at or before the watermark was settled by an earlier pass,
        // so only the first pass after a restart walks the whole conversation.
        var settledConversation = history.ConversationAfter(_settledConversationSequence);
        var batches = settledConversation.Where(item => item.Role == LLMRole.Assistant && item.ToolCalls.Count > 0).ToArray();
        var terminals = history.ToolTerminalsOfCalls(
                [.. batches.SelectMany(batch => batch.ToolCalls).Select(call => call.Id)])
            .ToDictionary(terminal => terminal.ToolCallId, StringComparer.Ordinal);

        foreach (var batch in batches)
        {
            var imageBudget = new ToolCycleImageBudget(requestLimits.ImageBytesPerToolCycle, promptTemplates);
            var hasPendingCalls = batch.ToolCalls.Any(call => !terminals.ContainsKey(call.Id));
            foreach (var settledCall in batch.ToolCalls)
            {
                if (!hasPendingCalls || !terminals.TryGetValue(settledCall.Id, out var settledTerminal))
                {
                    continue;
                }

                if (settledTerminal.Status == ToolExecutionStatus.ImageBudgetExceeded)
                {
                    imageBudget.RestoreExceeded();
                }

                foreach (var part in settledTerminal.ResultParts.Where(part => part.Kind == ConversationPartKind.ImageArtifact))
                {
                    var artifact = history.ResolveImageArtifact(part.ArtifactId)
                        ?? throw new InvalidDataException("A settled tool image artifact is missing.");
                    imageBudget.RestoreAccepted(artifact.ByteLength);
                }
            }

            var stopped = false;
            var callIndex = 0;
            while (callIndex < batch.ToolCalls.Count)
            {
                var call = batch.ToolCalls[callIndex];
                if (terminals.TryGetValue(call.Id, out var restoredTerminal))
                {
                    RestoreToolSettlement(batch.Sequence, restoredTerminal);
                    stopped |= restoredTerminal.Status == ToolExecutionStatus.Cancelled;
                    callIndex++;
                    continue;
                }

                if (stopped || cancellationToken.IsCancellationRequested)
                {
                    var cancelled = CancelTool(call);
                    await SettleTool(batch.Sequence, cancelled, terminals).ConfigureAwait(false);
                    stopped = true;
                    callIndex++;
                    continue;
                }

                if (!IsParallelSafe(snapshot, batch.Sequence, call))
                {
                    var settlement = await Invoke(selection, snapshot, captureSecurity, batch.Sequence, call, imageBudget, cancellationToken)
                        .ConfigureAwait(false);
                    await SettleTool(batch.Sequence, settlement, terminals).ConfigureAwait(false);
                    stopped |= settlement.Terminal.Status == ToolExecutionStatus.Cancelled;
                    callIndex++;
                    continue;
                }

                var runEnd = callIndex;
                var executions = new Dictionary<string, Task<(Event Published, ToolExecutionTerminal Terminal)>>(
                    StringComparer.Ordinal);
                while (runEnd < batch.ToolCalls.Count)
                {
                    var candidate = batch.ToolCalls[runEnd];
                    if (!IsParallelSafe(snapshot, batch.Sequence, candidate))
                    {
                        break;
                    }

                    if (terminals.TryGetValue(candidate.Id, out var candidateTerminal))
                    {
                        if (candidateTerminal.Status == ToolExecutionStatus.Cancelled)
                        {
                            break;
                        }

                        runEnd++;
                        continue;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    executions.Add(
                        candidate.Id,
                        Invoke(selection, snapshot, captureSecurity, batch.Sequence, candidate, imageBudget, cancellationToken));
                    runEnd++;
                }

                if (runEnd == callIndex)
                {
                    continue;
                }

                var settlements = await Task.WhenAll(executions.Values).ConfigureAwait(false);
                var settlementsByCall = settlements.ToDictionary(
                    settlement => settlement.Terminal.ToolCallId,
                    StringComparer.Ordinal);
                while (callIndex < runEnd)
                {
                    call = batch.ToolCalls[callIndex];
                    if (terminals.TryGetValue(call.Id, out restoredTerminal))
                    {
                        RestoreToolSettlement(batch.Sequence, restoredTerminal);
                    }
                    else
                    {
                        var settlement = settlementsByCall[call.Id];
                        await SettleTool(batch.Sequence, settlement, terminals).ConfigureAwait(false);
                        stopped |= settlement.Terminal.Status == ToolExecutionStatus.Cancelled;
                    }

                    callIndex++;
                }
            }

            var images = batch.ToolCalls
                .Select(call => terminals[call.Id])
                .Where(terminal => terminal.Status == ToolExecutionStatus.Finished)
                .SelectMany(terminal => terminal.ResultParts)
                .Where(part => part.Kind == ConversationPartKind.ImageArtifact)
                .ToArray();
            if (images.Length > 0 && !history.HasToolSynthetic(batch.Sequence))
            {
                var published = new Event { Id = Identifier.EventId(), AgentSessionId = identity.SessionId };
                if (history.AppendToolSynthetic(published, batch.Sequence, images) is { } synthetic)
                {
                    conversation.Add(conversation.Materialize(synthetic));
                }

                await eventBroker.PublishWithCancellation(published, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (settledConversation.Count > 0)
        {
            _settledConversationSequence = settledConversation[^1].Sequence;
        }
    }

    private static bool IsParallelSafe(ToolSnapshot snapshot, long assistantSequence, LLMToolCall call)
    {
        var tool = snapshot.Find(call.Name);
        return tool is not null
            && tool.IsParallelSafe(new ToolInvocation(call.Id, call.ArgumentsJson, assistantSequence));
    }

    private void RestoreToolSettlement(long assistantSequence, ToolExecutionTerminal terminal)
    {
        if (history.AppendToolSettlement(
                new Event { Id = Identifier.EventId(), AgentSessionId = identity.SessionId },
                assistantSequence,
                terminal) is { } result)
        {
            conversation.Add(conversation.Materialize(result));
        }
    }

    private async Task SettleTool(
        long assistantSequence,
        (Event Published, ToolExecutionTerminal Terminal) settlement,
        Dictionary<string, ToolExecutionTerminal> terminals)
    {
        if (history.AppendToolSettlement(settlement.Published, assistantSequence, settlement.Terminal) is { } result)
        {
            conversation.Add(conversation.Materialize(result));
        }

        await eventBroker.PublishWithCancellation(settlement.Published, CancellationToken.None).ConfigureAwait(false);
        terminals.Add(settlement.Terminal.ToolCallId, settlement.Terminal);
    }

    private async Task<(Event Published, ToolExecutionTerminal Terminal)> Invoke(
        AgentTurnSelection selection,
        ToolSnapshot snapshot,
        Func<SecurityProfile> captureSecurity,
        long assistantSequence,
        LLMToolCall call,
        ToolCycleImageBudget imageBudget,
        CancellationToken cancellationToken)
    {
        var started = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = identity.SessionId,
            ToolStarted = new ToolStarted { ToolCallId = call.Id, ToolName = call.Name },
        };
        await eventEmitter.Emit(started, null, null, CancellationToken.None).ConfigureAwait(false);

        var tool = snapshot.Find(call.Name);
        var invocationStarted = Stopwatch.GetTimestamp();
        diagnostics.Write(new("tool", "started", DiagnosticSeverity.Information)
        {
            AgentSessionId = identity.SessionId,
            CorrelationId = call.Id,
            ToolName = tool?.Name,
        });
        if (tool is null)
        {
            diagnostics.Write(new("tool", "finished", DiagnosticSeverity.Warning)
            {
                AgentSessionId = identity.SessionId,
                CorrelationId = call.Id,
                Outcome = "unknown_tool",
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(invocationStarted).TotalMilliseconds,
            });
            return FailTool(call, $"unknown tool {call.Name}");
        }

        try
        {
            var invocationSelection = selection with { SecurityProfile = captureSecurity() };
            var invocation = new ToolInvocation(call.Id, call.ArgumentsJson, assistantSequence)
            {
                PromptTemplates = promptTemplates,
                ImageBudget = imageBudget,
            };
            ToolExecutionResult result;
            var execution = activity.BeginTool(tool.Name);
            try
            {
                usage.RecordToolUsage(assistantSequence, call);
                result = await tool.Execute(invocation, invocationSelection, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                activity.FinishTool(execution);
            }

            if (result.Outcome == ToolExecutionOutcome.ImageBudgetExceeded)
            {
                var (failed, failedTerminal) = FailTool(call, result.Text);
                return (failed, failedTerminal with { Status = ToolExecutionStatus.ImageBudgetExceeded });
            }

            var text = promptTemplates.Render(
                "tool-result.text",
                [new PromptTemplateArgument("value", result.Text)]);
            if (ToolOutputBlobStore.IsOversized(text))
            {
                text = await toolOutputBlobs.Persist(text, CancellationToken.None).ConfigureAwait(false);
            }

            var terminal = new ToolFinished { ToolCallId = call.Id, ToolName = call.Name, Result = text };
            terminal.Artifacts.AddRange(result.ImageArtifacts.Select(ArtifactReference.FromMetadata));
            if (result.YieldedProcess is { } yielded)
            {
                terminal.YieldedProcess = Protocol.YieldedShellProcess.FromProcess(yielded);
            }

            var finished = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = identity.SessionId,
                ToolFinished = terminal,
            };
            var parts = new List<ConversationPart> { ConversationPart.TextPart(text) };
            parts.AddRange(result.ImageArtifacts.Select(ConversationPart.ImageArtifact));
            diagnostics.Write(new("tool", "finished", DiagnosticSeverity.Information)
            {
                AgentSessionId = identity.SessionId,
                CorrelationId = call.Id,
                ToolName = tool.Name,
                Outcome = result.YieldedProcess is null ? "completed" : "yielded",
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(invocationStarted).TotalMilliseconds,
            });
            return (finished, new ToolExecutionTerminal(
                call.Id,
                call.Name,
                ToolExecutionStatus.Finished,
                parts,
                text));
        }
        catch (OperationCanceledException)
        {
            diagnostics.Write(new("tool", "finished", DiagnosticSeverity.Information)
            {
                AgentSessionId = identity.SessionId,
                CorrelationId = call.Id,
                ToolName = tool.Name,
                Outcome = "cancelled",
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(invocationStarted).TotalMilliseconds,
            });
            return CancelTool(call);
        }
        catch (Exception failure)
        {
            diagnostics.Write(new("tool", "finished", DiagnosticSeverity.Error)
            {
                AgentSessionId = identity.SessionId,
                CorrelationId = call.Id,
                ToolName = tool.Name,
                Outcome = "failed",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(invocationStarted).TotalMilliseconds,
            });
            return FailTool(call, failure.Message);
        }
    }

    private (Event Published, ToolExecutionTerminal Terminal) CancelTool(LLMToolCall call)
    {
        var cancelled = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = identity.SessionId,
            ToolCancelled = new ToolCancelled { ToolCallId = call.Id, ToolName = call.Name },
        };
        return (cancelled, new ToolExecutionTerminal(
            call.Id,
            call.Name,
            ToolExecutionStatus.Cancelled,
            [ConversationPart.TextPart(_interruptedResult)],
            _interruptedResult));
    }

    private (Event Published, ToolExecutionTerminal Terminal) FailTool(LLMToolCall call, string message)
    {
        var failed = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = identity.SessionId,
            ToolError = new ToolError { ToolCallId = call.Id, ToolName = call.Name, Message = message },
        };
        var result = promptTemplates.Render(
            "tool-result.error",
            [new PromptTemplateArgument("message", message)]);
        return (failed, new ToolExecutionTerminal(
            call.Id,
            call.Name,
            ToolExecutionStatus.Error,
            [ConversationPart.TextPart(result)],
            result));
    }
}
