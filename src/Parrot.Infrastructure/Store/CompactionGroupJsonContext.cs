using System.Text.Json.Serialization;

namespace Parrot.Store;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(CompactionGroupArtifact))]
[JsonSerializable(typeof(CompactionGroupArtifactMessage))]
[JsonSerializable(typeof(CompactionGroupArtifactContent))]
[JsonSerializable(typeof(CompactionGroupArtifactToolCall))]
internal sealed partial class CompactionGroupJsonContext : JsonSerializerContext;
