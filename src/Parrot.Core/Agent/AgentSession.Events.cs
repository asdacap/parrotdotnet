using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed partial class AgentSession
{
    private static MessageContentPart ToProtocol(ConversationPart part) => part.Kind switch
    {
        ConversationPartKind.Text => new MessageContentPart { Text = part.Text },
        ConversationPartKind.ImageArtifact => new MessageContentPart { ArtifactId = part.ArtifactId },
        _ => throw new InvalidOperationException($"unsupported conversation part {part.Kind}"),
    };

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
        var usage = eventRepository.Append(published, role, content);
        await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
        if (usage is not null)
        {
            await eventBroker.Publish(new Event { SessionUsageSnapshot = SessionUsageSnapshot.From(usage) }, cancellationToken).ConfigureAwait(false);
        }
    }
}
