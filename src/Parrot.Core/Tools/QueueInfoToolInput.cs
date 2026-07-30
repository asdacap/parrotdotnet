using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class QueueInfoToolInput
{
    [Description("Name of the queue whose metadata and size to retrieve.")]
    [JsonPropertyName("name")]
    [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    [ToolRequired]
    public string? Name { get; init; }
}
