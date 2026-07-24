using System.Text.Json.Serialization;

namespace Parrot.Llm;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireRequest))]
[JsonSerializable(typeof(ChatCompletionsWire))]
public sealed partial class LlmJsonContext : JsonSerializerContext;
