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

    public async Task Run(string model, string prompt, CancellationToken cancellationToken)
    {
        _taskId = Identifier.New();

        await events.Publish(Compose(EventKind.TurnStart, $"turn started ({model})"), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var result = await provider.Call(
                new LLMRequest
                {
                    Model = model,
                    MaxTokens = 4096,
                    Messages = [new LLMMessage(LLMRole.User, prompt)],
                },
                this,
                cancellationToken).ConfigureAwait(false);

            var summary = result.FinishReason == "length" && result.Text.Length == 0
                ? "turn ended: the token budget was spent on reasoning before any content"
                : $"turn ended ({result.FinishReason}, {result.Usage.InputTokens} in / {result.Usage.OutputTokens} out)";

            await events.Publish(Compose(EventKind.TurnEnd, summary), cancellationToken).ConfigureAwait(false);
        }
        catch (LLMProviderException failure)
        {
            // A provider boundary is a deliberate containment point: the turn
            // reports and ends rather than taking the process down.
            await events.Publish(Compose(EventKind.Error, failure.Message), cancellationToken).ConfigureAwait(false);
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

        return events.Publish(Compose(kind, llmEvent.Text), cancellationToken);
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
