using System.Text.Json.Serialization;

namespace Parrot.Llm.Wire;

// Source generation only (MIGRATION.md section 2). Request bodies are serialized
// through these roots; response streams are parsed with JsonDocument, so no
// chunk DTOs are registered here.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnthropicMessagesAdapter.Body), TypeInfoPropertyName = "AnthropicMessagesBody")]
[JsonSerializable(typeof(List<AnthropicMessagesAdapter.ContentBlock>), TypeInfoPropertyName = "AnthropicContentBlocks")]
[JsonSerializable(typeof(ChatCompletionsAdapter.ReasoningReplay), TypeInfoPropertyName = "ChatReasoningReplay")]
[JsonSerializable(typeof(ChatCompletionsAdapter.Body), TypeInfoPropertyName = "ChatCompletionsBody")]
[JsonSerializable(typeof(ResponsesAdapter.NativeReplay), TypeInfoPropertyName = "ResponsesNativeReplay")]
[JsonSerializable(typeof(System.Text.Json.JsonElement), TypeInfoPropertyName = "JsonElement")]
[JsonSerializable(typeof(ResponsesAdapter.Body), TypeInfoPropertyName = "ResponsesBody")]
[JsonSerializable(typeof(ResponsesAdapter.WebSocketRequest), TypeInfoPropertyName = "ResponsesWebSocketRequest")]
[JsonSerializable(typeof(ResponsesAdapter.InputItem), TypeInfoPropertyName = "ResponsesInputItem")]
[JsonSerializable(typeof(IReadOnlyList<ResponsesAdapter.FunctionTool>), TypeInfoPropertyName = "ResponsesFunctionTools")]
[JsonSerializable(typeof(ResponsesAdapter.Reasoning), TypeInfoPropertyName = "ResponsesReasoning")]
[JsonSerializable(typeof(string), TypeInfoPropertyName = "String")]
[JsonSerializable(typeof(List<ChatCompletionsAdapter.ChatContentPart>), TypeInfoPropertyName = "ChatContentParts")]
internal sealed partial class WireJsonContext : JsonSerializerContext;
