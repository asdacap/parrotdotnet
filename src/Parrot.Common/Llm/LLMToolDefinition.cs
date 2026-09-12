namespace Parrot.Llm;

// A tool offered to the model. ParametersJson is a JSON Schema object as a raw
// string -- the tool owns its schema, the provider only forwards it.
internal sealed record LLMToolDefinition(string Name, string Description, string ParametersJson);
