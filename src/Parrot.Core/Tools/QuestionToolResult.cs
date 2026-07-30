using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class QuestionToolResult
{
    [JsonPropertyName("answers")]
    public required QuestionWireAnswer[] Answers { get; init; }
}
