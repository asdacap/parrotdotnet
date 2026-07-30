using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class TodoWriteInput
{
    [Description("Complete ordered todo list that replaces the current list.")]
    [JsonPropertyName("todos")]
    [ToolRequired]
    public TodoWriteItem[]? Todos { get; init; }
}
