using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;
using ToolSnapshot = Parrot.Tools.ToolSnapshot;

namespace Parrot.Agent;

// Streams one provider call: deltas go out as events, and the terminal
// Completed is returned so the turn can decide what to do next.
internal sealed class ProviderCall(
    AgentIdentity identity,
    ProviderSessions providerSessions,
    ProviderTokenBudget providerTokenBudget,
    IEventBroker eventBroker,
    AgentOutputFile outputFile,
    AgentSessionActivity activity,
    AgentEventEmitter eventEmitter,
    AgentUsageRecorder usage)
{
    public async Task<LLMEvent> Stream(
        AgentTurnSelection? selection,
        ToolSnapshot snapshot,
        string instructions,
        IReadOnlyList<LLMMessage> messages,
        CancellationToken cancellationToken)
    {
        var selectedModel = selection?.ResolvedModel.CanonicalModel
            ?? throw new AgentRegistryException("turn selection is unavailable");
        var estimatedInputTokens = Compactor.EstimateInputTokens(selectedModel, instructions, snapshot.Definitions, messages);
        var maximumOutputTokens = providerTokenBudget.CalculateMaximumOutputTokens(
            selectedModel.Selector,
            estimatedInputTokens,
            selectedModel.Model);

        var request = new LLMRequest
        {
            Model = selectedModel.ModelId,
            MaxOutputTokens = maximumOutputTokens,
            Instructions = instructions,
            Messages = messages,
            Tools = snapshot.Definitions,
            Reasoning = selectedModel.Reasoning,
        };

        var completed = LLMEvent.Completed(string.Empty, 0, 0, 0, string.Empty, []);
        uint requestAttempt = 0;
        var awaitingFirstData = false;
        var streamedText = false;
        var fragments = new StreamedFragmentBuffer();
        var events = providerSessions.Get(selectedModel.Provider)
            .Call(request, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            activity.BeginProviderRequest();
            while (await MoveNextFlushingFragments(events, fragments, cancellationToken).ConfigureAwait(false))
            {
                var llmEvent = events.Current;
                if (llmEvent.Kind is not (LLMEventKind.TextDelta or LLMEventKind.ReasoningDelta))
                {
                    await CommitProviderEvent(fragments.EndStream(), cancellationToken).ConfigureAwait(false);
                }

                if (llmEvent.Kind is LLMEventKind.HttpRequestStarted or LLMEventKind.HttpResponseHeadersReceived)
                {
                    if (llmEvent.Kind == LLMEventKind.HttpRequestStarted)
                    {
                        requestAttempt++;
                    }

                    awaitingFirstData = llmEvent.Kind == LLMEventKind.HttpResponseHeadersReceived;
                    PublishProviderRequestPhase(
                        llmEvent.Kind == LLMEventKind.HttpRequestStarted
                            ? ProviderRequestPhase.Requesting
                            : ProviderRequestPhase.HeadersReceived,
                        requestAttempt);
                    continue;
                }

                if (llmEvent.Kind == LLMEventKind.Retry)
                {
                    awaitingFirstData = false;
                    PublishProviderRequestPhase(ProviderRequestPhase.Idle, requestAttempt);
                    if (streamedText)
                    {
                        await outputFile.Append("\n[retry]\n").ConfigureAwait(false);
                        streamedText = false;
                    }
                }

                if (awaitingFirstData && llmEvent.Kind switch
                {
                    LLMEventKind.TextDelta or LLMEventKind.ReasoningDelta => llmEvent.Text.Length > 0,
                    LLMEventKind.ToolCallDelta => llmEvent.ToolCallId.Length > 0
                        || llmEvent.ToolName.Length > 0 || llmEvent.Text.Length > 0,
                    _ => false,
                })
                {
                    awaitingFirstData = false;
                    PublishProviderRequestPhase(ProviderRequestPhase.Idle, requestAttempt);
                }

                if (llmEvent.Kind == LLMEventKind.Completed)
                {
                    providerTokenBudget.ObserveUsage(selectedModel.Selector, estimatedInputTokens, llmEvent);
                    usage.RecordRequestUsage(selectedModel, request.Reasoning, llmEvent);
                    eventBroker.PublishTransient(
                        new Event
                        {
                            Id = Identifier.EventId(),
                            AgentSessionId = identity.SessionId,
                            ProviderCallUsage = new ProviderCallUsage
                            {
                                InputTokens = Math.Max(0, llmEvent.InputTokens),
                                OutputTokens = Math.Max(0, llmEvent.OutputTokens),
                            },
                        });
                    completed = llmEvent;
                    continue;
                }

                streamedText |= llmEvent.Kind == LLMEventKind.TextDelta && llmEvent.Text.Length > 0;
                activity.ObserveProviderEvent(llmEvent);
                foreach (var ready in fragments.Add(TranslateProviderEvent(llmEvent)))
                {
                    await CommitProviderEvent(ready, cancellationToken).ConfigureAwait(false);
                }
            }

            await CommitProviderEvent(fragments.EndStream(), cancellationToken).ConfigureAwait(false);
            return completed;
        }
        catch (Exception)
        {
            // What was streamed before the failure is still kept and shown.
            await CommitProviderEvent(fragments.EndStream(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await events.DisposeAsync().ConfigureAwait(false);
            PublishProviderRequestPhase(ProviderRequestPhase.Idle, requestAttempt);
            activity.FinishProviderRequest();
            if (streamedText)
            {
                await outputFile.Append("\n").ConfigureAwait(false);
            }
        }
    }

    // Reports a retry from a provider call made outside a turn, such as compaction's.
    public ValueTask EmitRetry(LLMEvent llmEvent, CancellationToken cancellationToken) =>
        eventEmitter.Emit(TranslateProviderEvent(llmEvent), null, null, cancellationToken);

    // A stalled stream must not keep a held fragment past its hold, so waiting
    // for the next event also waits on the hold.
    private async Task<bool> MoveNextFlushingFragments(
        IAsyncEnumerator<LLMEvent> events,
        StreamedFragmentBuffer fragments,
        CancellationToken cancellationToken)
    {
        var next = events.MoveNextAsync();
        if (next.IsCompleted || !fragments.IsHolding)
        {
            return await next.ConfigureAwait(false);
        }

        var pending = next.AsTask();
        using (var hold = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var held = Task.Delay(fragments.RemainingHold, hold.Token);
            if (await Task.WhenAny(pending, held).ConfigureAwait(false) == held && held.IsCompletedSuccessfully)
            {
                await CommitProviderEvent(fragments.Take(), cancellationToken).ConfigureAwait(false);
            }

            await hold.CancelAsync().ConfigureAwait(false);
        }

        return await pending.ConfigureAwait(false);
    }

    private async Task CommitProviderEvent(Event? published, CancellationToken cancellationToken)
    {
        if (published is null)
        {
            return;
        }

        if (published.PayloadCase == Event.PayloadOneofCase.TextChunk && published.TextChunk.Fragment.Length > 0)
        {
            await outputFile.Append(published.TextChunk.Fragment).ConfigureAwait(false);
        }

        await eventEmitter.Emit(published, null, null, cancellationToken).ConfigureAwait(false);
    }

    private void PublishProviderRequestPhase(ProviderRequestPhase phase, uint attempt) =>
        eventBroker.PublishTransient(new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = identity.SessionId,
            ProviderRequestPhaseChanged = new ProviderRequestPhaseChangedEvent { Phase = phase, Attempt = attempt },
        });

    private Event TranslateProviderEvent(LLMEvent llmEvent)
    {
        var published = new Event { Id = Identifier.EventId(), AgentSessionId = identity.SessionId };

        switch (llmEvent.Kind)
        {
            case LLMEventKind.TextDelta:
                published.TextChunk = new TextChunk { Fragment = llmEvent.Text };
                break;

            case LLMEventKind.ReasoningDelta:
                published.ReasoningChunk = new ReasoningChunk
                {
                    Fragment = llmEvent.Text,
                    Kind = llmEvent.ReasoningKind == LLMReasoningKind.Summary
                        ? ReasoningKind.Summary
                        : ReasoningKind.Raw,
                    PartId = llmEvent.ReasoningPartId,
                    Completed = llmEvent.ReasoningCompleted,
                };
                break;

            case LLMEventKind.ToolCallDelta:
                published.ToolCallChunk = new ToolCallChunk
                {
                    ToolCallId = llmEvent.ToolCallId,
                    ToolName = llmEvent.ToolName,
                    ArgumentsFragment = llmEvent.Text,
                };
                break;

            default:
                published.RetryNotice = new RetryNotice
                {
                    Attempt = llmEvent.Attempt,
                    RetryAfterMs = (int)llmEvent.RetryAfter.TotalMilliseconds,
                    Reason = llmEvent.Text,
                };
                break;
        }

        return published;
    }
}
