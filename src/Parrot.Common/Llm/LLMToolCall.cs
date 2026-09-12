namespace Parrot.Llm;

// One call the model wants made: the id it will match the result against, the
// tool name, and its arguments as a JSON string (assembled from the stream's
// fragments).
internal sealed record LLMToolCall(string Id, string Name, string ArgumentsJson);
