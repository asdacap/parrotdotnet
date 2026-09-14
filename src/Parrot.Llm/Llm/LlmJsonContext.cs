using System.Text.Json.Serialization;

namespace Parrot.Llm;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatCompletionsWire))]
[JsonSerializable(typeof(WireModelList))]
internal sealed partial class LlmJsonContext : JsonSerializerContext;
