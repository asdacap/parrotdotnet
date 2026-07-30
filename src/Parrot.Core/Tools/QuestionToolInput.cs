using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class QuestionToolInput
{
    [JsonPropertyName("questions")]
    public QuestionWireDefinition[]? Questions { get; init; }
}
