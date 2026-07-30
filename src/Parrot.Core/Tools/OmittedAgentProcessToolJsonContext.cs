using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSerializable(typeof(InterruptProcessToolInput))]
[JsonSerializable(typeof(WaitProcessToolInput))]
internal sealed partial class OmittedAgentProcessToolJsonContext : JsonSerializerContext;
