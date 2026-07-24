using System.Text.Json.Serialization;

namespace Parrot.Llm;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireRequest))]
[JsonSerializable(typeof(ChatCompletionsWire))]
internal sealed partial class LlmJsonContext : JsonSerializerContext;
