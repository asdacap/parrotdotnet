using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Agent;

// M1: one turn, no tools, no compaction, no persistence. It is already the
// thing that attaches identity, which is the part the provider structurally
// cannot do.
internal sealed class AgentSession(string sessionId, ILLMProvider provider, EventBroker events) : ILLMEventSink
{
    private string _taskId = string.Empty;

    public string SessionId { get; } = sessionId;

    // Selection is session state: an UpdateSession changes it, a prompt does not.
    public string Model { get; set; } = string.Empty;

    public async Task Run(string taskId, string prompt, CancellationToken cancellationToken)
    {
        _taskId = taskId;

        var started = Compose(EventKind.TurnStart, $"turn started ({Model})");
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

            var summary = result.FinishReason == "length" && result.Text.Length == 0
                ? "turn ended: the token budget was spent on reasoning before any content"
                : $"turn ended ({result.FinishReason}, {result.Usage.InputTokens} in / {result.Usage.OutputTokens} out)";

            var ended = Compose(EventKind.TurnEnd, summary);
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
            var failed = Compose(EventKind.Error, failure.Message);
            failed.TurnFailed = new TurnFailed { Message = failure.Message };
            await events.Publish(failed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            events.Complete();
        }
    }

    public ValueTask Publish(LLMEvent llmEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(llmEvent);

        var kind = llmEvent.Kind switch
        {
            LLMEventKind.TextDelta => EventKind.Text,
            LLMEventKind.ReasoningDelta => EventKind.Reasoning,
            LLMEventKind.ToolCallDelta => EventKind.ToolCall,
            _ => EventKind.Retry,
        };

        var published = Compose(kind, llmEvent.Text);

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

    private Event Compose(EventKind kind, string text) =>
        new()
        {
            Id = Identifier.New(),
            SessionId = SessionId,
            TaskId = _taskId,
            Kind = kind,
            Text = text,
        };
}
