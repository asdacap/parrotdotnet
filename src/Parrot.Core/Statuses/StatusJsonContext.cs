using System.Text.Json.Serialization;

namespace Parrot.Statuses;

[JsonSerializable(typeof(string))]
internal sealed partial class StatusJsonContext : JsonSerializerContext;
