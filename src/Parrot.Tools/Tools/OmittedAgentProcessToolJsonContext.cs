using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSerializable(typeof(InterruptProcessTool.Input), TypeInfoPropertyName = "InterruptProcessToolInput")]
[JsonSerializable(typeof(WriteStdinTool.Input), TypeInfoPropertyName = "WriteStdinToolInput")]
internal sealed partial class OmittedAgentProcessToolJsonContext : JsonSerializerContext;
