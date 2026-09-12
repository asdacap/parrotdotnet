using System.Text.Json.Serialization;

namespace Parrot.Llm.Wire;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ImageGenerationWireRequest))]
[JsonSerializable(typeof(ImageGenerationWireResponse))]
internal sealed partial class ImageGenerationJsonContext : JsonSerializerContext;
