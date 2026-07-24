using System.Text.Json.Serialization;

namespace Parrot.Llm.Wire;

// Source generation only (MIGRATION.md section 2). Request bodies are serialized
// through these roots; response streams are parsed with JsonDocument, so no
// chunk DTOs are registered here.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatCompletionsAdapter.Body), TypeInfoPropertyName = "ChatCompletionsBody")]
[JsonSerializable(typeof(ResponsesAdapter.Body), TypeInfoPropertyName = "ResponsesBody")]
internal sealed partial class WireJsonContext : JsonSerializerContext;
