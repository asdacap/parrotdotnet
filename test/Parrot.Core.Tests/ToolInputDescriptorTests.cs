using System.Buffers;
using System.Text;
using System.Text.Json;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ToolInputDescriptorTests : IDisposable
{
    private const string RequestWritePermissionToolSchema =
        "{\"type\":\"object\",\"properties\":{\"paths\":{\"type\":\"array\",\"minItems\":1,\"items\":{\"type\":\"string\",\"minLength\":1}},\"reason\":{\"type\":\"string\",\"minLength\":1}},\"required\":[\"paths\",\"reason\"],\"additionalProperties\":false}";

    private const string WaitToolSchema =
        "{\"type\":\"object\",\"properties\":{\"duration_ms\":{\"type\":\"integer\",\"minimum\":10000,\"maximum\":4294967294,\"default\":10000}},\"additionalProperties\":false}";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "parrot-tool-documentation-tests",
        Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public async Task Every_tool_schema_is_structural_before_documentation_and_complete_after_hydration()
    {
        IReadOnlyList<ITool> tools =
        [
            new SchemaTool("agent_send", AgentSendTool.Input.Descriptor),
            new SchemaTool("agent_spawn", AgentSpawnTool.Input.Descriptor),
            new SchemaTool("edit", EditTool.Input.Descriptor),
            new SchemaTool("exec_command", ExecCommandTool.Input.Descriptor),
            new SchemaTool("glob", GlobTool.Input.Descriptor),
            new SchemaTool("grep", GrepTool.Input.Descriptor),
            new SchemaTool("interrupt_process", InterruptProcessTool.Input.Descriptor),
            new SchemaTool("question", QuestionTool.Input.Descriptor),
            new SchemaTool("queue_create", QueueCreateTool.Input.Descriptor),
            new SchemaTool("queue_info", QueueInfoTool.Input.Descriptor),
            new SchemaTool("queue_listen", QueueListenTool.Input.Descriptor),
            new SchemaTool("queue_push", QueuePushTool.Input.Descriptor),
            new SchemaTool("queue_take", QueueTakeTool.Input.Descriptor),
            new SchemaTool("read", ReadTool.Input.Descriptor),
            new SchemaTool("read_image", ReadImageTool.Input.Descriptor),
            new SchemaTool("request_write_permission", RequestWritePermissionToolSchema),
            new SchemaTool("set_checkpoint", SetCheckpointTool.Input.Descriptor),
            new SchemaTool("status", StatusTool.Input.Descriptor),
            new SchemaTool("todoread", TodoReadTool.Input.Descriptor),
            new SchemaTool("todowrite", TodoWriteTool.Input.Descriptor),
            new SchemaTool("wait", WaitToolSchema),
            new SchemaTool("wait_agent", WaitAgentTool.Input.Descriptor),
            new SchemaTool("wait_process", WaitProcessTool.Input.Descriptor),
            new SchemaTool("web_fetch", WebFetchTool.Input.Descriptor),
            new SchemaTool("write_stdin", WriteStdinTool.Input.Descriptor),
            new SchemaTool("write", WriteTool.Input.Descriptor),
        ];
        var configuration = Configuration.Load(
            Path.Combine(_directory, "config.yaml"),
            Path.Combine(_directory, "predefined_config.yaml"));

        _ = await Assert.That(tools.Count).IsEqualTo(26);
        _ = await Assert.That(string.Join(',', tools.Select(static tool => tool.Name))).IsEqualTo(
            "agent_send,agent_spawn,edit,exec_command,glob,grep,interrupt_process,question,queue_create,queue_info,queue_listen,queue_push,queue_take,read,read_image,request_write_permission,set_checkpoint,status,todoread,todowrite,wait,wait_agent,wait_process,web_fetch,write_stdin,write");

        foreach (var tool in tools)
        {
            using var structural = JsonDocument.Parse(tool.ParametersJson);
            await AssertStructuralSchema(structural.RootElement);
        }

        var definitions = configuration.ToolDocumentation.Document(tools);
        _ = await Assert.That(definitions.Count).IsEqualTo(tools.Count);
        foreach (var index in Enumerable.Range(0, tools.Count))
        {
            var tool = tools[index];
            var definition = definitions[index];
            var documentation = configuration.ToolDocumentation.Tools[tool.Name];
            _ = await Assert.That(definition.Name).IsEqualTo(tool.Name);
            _ = await Assert.That(definition.Description).IsEqualTo(documentation.Description);

            using var structural = JsonDocument.Parse(tool.ParametersJson);
            using var hydrated = JsonDocument.Parse(definition.ParametersJson);
            _ = await Assert.That(hydrated.RootElement.TryGetProperty("description", out _)).IsFalse();
            _ = await Assert.That(WithoutDescriptions(structural.RootElement))
                .IsEqualTo(WithoutDescriptions(hydrated.RootElement));
            await AssertHydratedSchema(hydrated.RootElement, documentation.Parameters);
        }
    }

    private static async Task AssertStructuralSchema(JsonElement schema)
    {
        _ = await Assert.That(schema.ValueKind).IsEqualTo(JsonValueKind.Object);
        _ = await Assert.That(schema.TryGetProperty("description", out _)).IsFalse();
        if (schema.TryGetProperty("type", out var type))
        {
            _ = await Assert.That(type.GetString()).IsEqualTo("object");
        }

        await AssertNoDescriptions(schema);
    }

    private static async Task AssertHydratedSchema(
        JsonElement schema,
        IReadOnlyDictionary<string, ToolParameterDocumentation> documentation)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            _ = await Assert.That(properties.EnumerateObject().Select(static property => property.Name)
                .SequenceEqual(documentation.Keys)).IsTrue();
            foreach (var property in properties.EnumerateObject())
            {
                var parameter = documentation[property.Name];
                _ = await Assert.That(property.Value.GetProperty("description").GetString())
                    .IsEqualTo(parameter.Description);
                await AssertHydratedSchema(property.Value, parameter.Properties);
            }
        }
        else if (!schema.TryGetProperty("items", out _))
        {
            _ = await Assert.That(documentation.Count).IsEqualTo(0);
        }

        if (schema.TryGetProperty("items", out var items))
        {
            await AssertHydratedSchema(items, documentation);
        }
    }

    private static async Task AssertNoDescriptions(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in schema.EnumerateObject())
            {
                _ = await Assert.That(property.Name).IsNotEqualTo("description");
                if (string.Equals(property.Name, "properties", StringComparison.Ordinal))
                {
                    foreach (var schemaProperty in property.Value.EnumerateObject())
                    {
                        await AssertNoDescriptions(schemaProperty.Value);
                    }
                }
                else
                {
                    await AssertNoDescriptions(property.Value);
                }
            }
        }
        else if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schema.EnumerateArray())
            {
                await AssertNoDescriptions(item);
            }
        }
    }

    private static string WithoutDescriptions(JsonElement schema)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteWithoutDescriptions(writer, schema);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteWithoutDescriptions(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "description", StringComparison.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteWithoutDescriptions(writer, property.Value);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteWithoutDescriptions(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private sealed class SchemaTool(string name, string parametersJson) : ITool
    {
        public string Name => name;

        public string ParametersJson => parametersJson;

        public Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
