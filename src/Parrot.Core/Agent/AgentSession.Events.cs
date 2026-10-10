using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Agent;

internal sealed partial class AgentSession
{
    private Event TranslateProviderEvent(LLMEvent llmEvent)
    {
        var published = new Event { Id = Identifier.EventId(), AgentSessionId = SessionId };

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

    // Commits the event and its projection, then publishes it. In that order:
    // EventBroker must only ever hand a subscriber an event the repository has
    // already committed, so nothing observable can be un-happened by a crash.
    private async ValueTask EmitEvent(
        Event published,
        string? role,
        string? content,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = agentHistory.Append(published, role, content);
        }
        catch (Exception failure)
        {
            diagnostics.Write(new("persistence", "append_failed", DiagnosticSeverity.Error)
            {
                AgentSessionId = SessionId,
                CorrelationId = published.Id,
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }

        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
    }
}
