using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class QuestionWireDefinition
{
    [Description("Stable identifier used to match the user's answer to this question.")]
    [JsonPropertyName("id")]
    [ToolMinLength(1)]
    [ToolRequired]
    public string? Id { get; init; }

    [Description("Short heading displayed with the question.")]
    [JsonPropertyName("header")]
    public string? Header { get; init; }

    [Description("Question text shown to the user.")]
    [JsonPropertyName("prompt")]
    [ToolMinLength(1)]
    [ToolRequired]
    public string? Prompt { get; init; }

    [Description("Selectable answers offered to the user.")]
    [JsonPropertyName("options")]
    public QuestionWireOption[]? Options { get; init; }

    [Description("Whether the user may select more than one answer.")]
    [JsonPropertyName("multiple")]
    public bool Multiple { get; init; }

    [Description("Whether the user may supply a custom answer.")]
    [JsonPropertyName("custom")]
    public bool Custom { get; init; }
}
