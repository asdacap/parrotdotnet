namespace Parrot.Llm;

internal sealed record LLMMessage(LLMRole Role, string Content);
