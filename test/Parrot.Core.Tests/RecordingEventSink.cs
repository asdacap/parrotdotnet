using Parrot.Llm;

namespace Parrot.Core.Tests;

// The sink being a parameter is what makes this possible at all: no broker, no
// container, no session.
internal sealed class RecordingEventSink : ILLMEventSink
{
    public List<LLMEvent> Events { get; } = [];

    public ValueTask Publish(LLMEvent llmEvent, CancellationToken cancellationToken)
    {
        Events.Add(llmEvent);
        return ValueTask.CompletedTask;
    }
}
