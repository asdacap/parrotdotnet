using System.Text.Json.Serialization;

namespace Parrot.Queues;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(QueueMetadata))]
[JsonSerializable(typeof(string))]
internal sealed partial class QueuesJsonContext : JsonSerializerContext;
