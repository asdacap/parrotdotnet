namespace Parrot.Llm;

public interface ILLMEventSink
{
    ValueTask Publish(LLMEvent llmEvent, CancellationToken cancellationToken);
}
