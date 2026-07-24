using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

// M1: one turn, no tools, no compaction, no persistence. It is already the
// thing that attaches identity, which is the part the provider structurally
// cannot do.
internal sealed class AgentSession(
    string sessionId,
    ILLMProvider provider,
    EventBroker eventBroker,
    EventRepository eventRepository)
{
    public string SessionId { get; } = sessionId;

    // Selection is session state: an UpdateSession changes it, a prompt does not.
    public string Model { get; set; } = string.Empty;

    // The turn is started, not awaited: admitting a prompt does not wait for it,
    // and the event stream stays open afterwards for whatever comes next. Run
    // lets nothing escape, so discarding the task loses nothing.
    public void Start(string prompt, CancellationToken cancellationToken) =>
        _ = Run(prompt, cancellationToken);

    internal Event Translate(LLMEvent llmEvent)
    {
        var published = Compose();

        switch (llmEvent.Kind)
        {
            case LLMEventKind.TextDelta:
                published.TextChunk = new TextChunk { Fragment = llmEvent.Text };
                break;

            case LLMEventKind.ReasoningDelta:
                published.ReasoningChunk = new ReasoningChunk { Fragment = llmEvent.Text };
                break;

            case LLMEventKind.ToolCallDelta:
                published.ToolCallChunk = new ToolCallChunk
                {
                    ToolCallId = llmEvent.ToolCallId,
                    ToolName = llmEvent.ToolName,
                    ArgumentsFragment = llmEvent.Text,
                };
                break;

            case LLMEventKind.Completed:
                published.TurnEnded = new TurnEnded
                {
                    FinishReason = llmEvent.FinishReason,
                    InputTokens = llmEvent.InputTokens,
                    OutputTokens = llmEvent.OutputTokens,
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

    private async Task Run(string prompt, CancellationToken cancellationToken)
    {
        // The prompt is durable before execution is requested (principle 1).
        var started = Compose();
        started.TurnStarted = new TurnStarted { Model = Model };
        await EmitEvent(started, "user", prompt, cancellationToken).ConfigureAwait(false);

        var request = new LLMRequest
        {
            Model = Model,
            MaxTokens = 4096,
            Messages = [LLMMessage.User(prompt)],
        };

        try
        {
            var spoken = new System.Text.StringBuilder();

            await foreach (var llmEvent in provider.Call(request, cancellationToken).ConfigureAwait(false))
            {
                if (llmEvent.Kind == LLMEventKind.TextDelta)
                {
                    _ = spoken.Append(llmEvent.Text);
                }

                var role = llmEvent.Kind == LLMEventKind.Completed ? "assistant" : null;
                var content = llmEvent.Kind == LLMEventKind.Completed ? spoken.ToString() : null;

                await EmitEvent(Translate(llmEvent), role, content, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            // A provider boundary is a deliberate containment point, and this
            // one is total: nothing started here is awaited, so an escaping
            // exception would be unobserved rather than reported.
            var failed = Compose();
            failed.TurnFailed = new TurnFailed { Message = failure.Message };
            await EmitEvent(failed, null, null, cancellationToken).ConfigureAwait(false);
        }
    }

    // Commits the event and its projection, then publishes it. In that order:
    // EventBroker must only ever hand a subscriber an event the repository has
    // already committed, so nothing observable can be un-happened by a crash.
    private async ValueTask EmitEvent(
        Event published,
        string? role,
        string? content,
        CancellationToken cancellationToken)
    {
        eventRepository.Append(published, role, content);
        await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
    }

    private Event Compose() =>
        new() { Id = Identifier.EventId(), AgentSessionId = SessionId };
}
