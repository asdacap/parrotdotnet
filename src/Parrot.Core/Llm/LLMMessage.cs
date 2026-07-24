namespace Parrot.Llm;

public sealed record LLMMessage(LLMRole Role, string Content);
