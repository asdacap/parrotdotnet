using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class TodoWriteItem
{
    [Description("Stable todo identifier. Omit it when adding a new todo.")]
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [Description("Todo text.")]
    [JsonPropertyName("content")]
    [ToolMinLength(1)]
    [ToolRequired]
    public string? Content { get; init; }

    [Description("Current todo state.")]
    [JsonPropertyName("status")]
    [ToolStringEnum("pending", "in_progress", "completed", "cancelled")]
    [ToolRequired]
    public string? Status { get; init; }

    [Description("Todo urgency.")]
    [JsonPropertyName("priority")]
    [ToolStringEnum("high", "medium", "low")]
    [ToolRequired]
    public string? Priority { get; init; }
}
