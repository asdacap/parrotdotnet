using System.Text.Json.Serialization;

namespace Parrot.Store;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionMeta))]
[JsonSerializable(typeof(OwnerRecord))]
[JsonSerializable(typeof(RootAgentNameReservation))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
