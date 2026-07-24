using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Agent;

// Owns the drain and the turn loop. A turn is a loop iteration here, not a
// nested Run: it calls the provider, executes any tool calls the model asks
// for, feeds the results back, and repeats until the model stops asking.
internal sealed class AgentSession(
    string sessionId,
    ILLMProvider provider,
    EventBroker eventBroker,
    EventRepository eventRepository,
    ToolRegistry tools,
    IToolContext toolContext)
{
    // A turn that keeps calling tools without ever finishing is a runaway, not
    // work. This bounds one prompt's tool round-trips.
    private const int MaxToolRounds = 24;

    public string SessionId { get; } = sessionId;

    // Selection is session state: an UpdateSession changes it, a prompt does not.
    public string Model { get; set; } = string.Empty;

    // The turn is started, not awaited: admitting a prompt does not wait for it,
    // and the event stream stays open afterwards. Run lets nothing escape, so
    // discarding the task loses nothing.
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
        var started = Compose();
        started.TurnStarted = new TurnStarted { Model = Model };

        // The prompt is durable before execution is requested (principle 1).
        await EmitEvent(started, "user", prompt, cancellationToken).ConfigureAwait(false);

        var snapshot = tools.Snapshot();
        var messages = new List<LLMMessage> { LLMMessage.User(prompt) };

        try
        {
            for (var round = 0; round < MaxToolRounds; round++)
            {
                var completed = await Provider(snapshot, messages, cancellationToken).ConfigureAwait(false);

                if (completed.ToolCalls.Count == 0)
                {
                    var ended = Compose();
                    ended.TurnEnded = new TurnEnded
                    {
                        FinishReason = completed.FinishReason,
                        InputTokens = completed.InputTokens,
                        OutputTokens = completed.OutputTokens,
                    };
                    await EmitEvent(ended, "assistant", completed.AssistantText, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                messages.Add(LLMMessage.Assistant(completed.AssistantText, completed.ToolCalls));

                foreach (var call in completed.ToolCalls)
                {
                    var result = await Invoke(snapshot, call, cancellationToken).ConfigureAwait(false);
                    messages.Add(LLMMessage.ToolResult(call.Id, result));
                }
            }

            await Fail("the turn exceeded its tool-call limit", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // A provider or tool boundary is a deliberate containment point, and
            // this one is total: nothing here is awaited, so an escaping
            // exception would be unobserved rather than reported.
            await Fail(failure.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    // Streams one provider call: deltas go out as events, and the terminal
    // Completed is returned so the loop can decide what to do next.
    private async Task<LLMEvent> Provider(
        ToolSnapshot snapshot, IReadOnlyList<LLMMessage> messages, CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = Model,
            MaxTokens = 4096,
            Messages = messages,
            Tools = snapshot.Definitions,
        };

        var completed = LLMEvent.Completed(string.Empty, 0, 0, string.Empty, []);

        await foreach (var llmEvent in provider.Call(request, cancellationToken).ConfigureAwait(false))
        {
            if (llmEvent.Kind == LLMEventKind.Completed)
            {
                completed = llmEvent;
                continue;
            }

            await EmitEvent(Translate(llmEvent), null, null, cancellationToken).ConfigureAwait(false);
        }

        return completed;
    }

    private async Task<string> Invoke(
        ToolSnapshot snapshot, LLMToolCall call, CancellationToken cancellationToken)
    {
        var running = Compose();
        running.ToolCallChunk = new ToolCallChunk { ToolCallId = call.Id, ToolName = call.Name };
        await EmitEvent(running, null, null, cancellationToken).ConfigureAwait(false);

        var tool = snapshot.Find(call.Name);

        return tool is null
            ? $"error: unknown tool {call.Name}"
            : await tool.Execute(call.ArgumentsJson, toolContext, cancellationToken).ConfigureAwait(false);
    }

    private async Task Fail(string message, CancellationToken cancellationToken)
    {
        var failed = Compose();
        failed.TurnFailed = new TurnFailed { Message = message };
        await EmitEvent(failed, null, null, cancellationToken).ConfigureAwait(false);
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
