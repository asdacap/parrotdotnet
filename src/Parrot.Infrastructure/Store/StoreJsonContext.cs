using System.Text.Json.Serialization;

namespace Parrot.Store;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionMeta))]
[JsonSerializable(typeof(OwnerRecord))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
