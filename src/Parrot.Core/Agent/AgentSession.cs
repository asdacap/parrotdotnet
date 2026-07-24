using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Agent;

// M1: one turn, no tools, no compaction, no persistence. It is already the
// thing that attaches identity, which is the part the provider structurally
// cannot do.
internal sealed class AgentSession(string sessionId, ILLMProvider provider, EventBroker events) : ILLMEventSink
{
    public string SessionId { get; } = sessionId;

    // Selection is session state: an UpdateSession changes it, a prompt does not.
    public string Model { get; set; } = string.Empty;

    public Task Turn { get; private set; } = Task.CompletedTask;

    public async Task Run(string prompt, CancellationToken cancellationToken)
    {
        var started = Compose();
        started.TurnStarted = new TurnStarted { Model = Model };
        await events.Publish(started, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await provider.Call(
                new LLMRequest
                {
                    Model = Model,
                    MaxTokens = 4096,
                    Messages = [new LLMMessage(LLMRole.User, prompt)],
                },
                this,
                cancellationToken).ConfigureAwait(false);

            var ended = Compose();
            ended.TurnEnded = new TurnEnded
            {
                FinishReason = result.FinishReason,
                InputTokens = result.Usage.InputTokens,
                OutputTokens = result.Usage.OutputTokens,
            };
            await events.Publish(ended, cancellationToken).ConfigureAwait(false);
        }
        catch (LLMProviderException failure)
        {
            // A provider boundary is a deliberate containment point: the turn
            // reports and ends rather than taking the process down.
            var failed = Compose();
            failed.TurnFailed = new TurnFailed { Message = failure.Message };
            await events.Publish(failed, cancellationToken).ConfigureAwait(false);
        }
    }

    // The turn is started, not awaited: admitting a prompt does not wait for it,
    // and the event stream stays open afterwards for whatever comes next.
    public void Start(string prompt, CancellationToken cancellationToken) =>
        Turn = Run(prompt, cancellationToken);

    public ValueTask Publish(LLMEvent llmEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(llmEvent);

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

            default:
                published.RetryNotice = new RetryNotice
                {
                    Attempt = llmEvent.Attempt,
                    RetryAfterMs = (int)llmEvent.RetryAfter.TotalMilliseconds,
                    Reason = llmEvent.Text,
                };
                break;
        }

        return events.Publish(published, cancellationToken);
    }

    private Event Compose() =>
        new() { Id = Identifier.New(), AgentSessionId = SessionId };
}
