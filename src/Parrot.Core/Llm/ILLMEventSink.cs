namespace Parrot.Llm;

internal interface ILLMEventSink
{
    ValueTask Publish(LLMEvent llmEvent, CancellationToken cancellationToken);
}
