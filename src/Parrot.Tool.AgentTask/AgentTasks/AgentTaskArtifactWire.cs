using System.Text.Json.Serialization;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskArtifactWire(
    [property: JsonPropertyName("schema_version")] int? SchemaVersion,
    [property: JsonPropertyName("tasks")] AgentTaskWire[]? Tasks);
