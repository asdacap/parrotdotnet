using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSerializable(typeof(InterruptProcessTool.Input), TypeInfoPropertyName = "InterruptProcessToolInput")]
[JsonSerializable(typeof(WaitProcessTool.Input), TypeInfoPropertyName = "WaitProcessToolInput")]
internal sealed partial class OmittedAgentProcessToolJsonContext : JsonSerializerContext;
