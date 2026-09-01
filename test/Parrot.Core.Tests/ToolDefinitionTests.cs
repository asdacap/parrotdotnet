using System.Text.Json;
using Parrot.Config;

namespace Parrot.Core.Tests;

internal sealed class ToolDefinitionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "parrot-tool-definition-tests",
        Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public async Task Every_builtin_tool_has_a_complete_configured_definition()
    {
        var configuration = Configuration.Load(
            Path.Combine(_directory, "config.yaml"),
            Path.Combine(_directory, "predefined_config.yaml"));
        var definitions = configuration.ToolDefinitions.Definitions;
        var expected = new[]
        {
            "agent_send", "agent_spawn", "edit", "exec_command", "glob", "grep", "interrupt_process",
            "question", "queue_create", "queue_info", "queue_listen", "queue_push", "queue_take", "read",
            "read_image", "request_write_permission", "run_agent_tasks", "set_checkpoint", "status", "todoread", "todowrite",
            "wait", "wait_agent", "wait_process", "web_fetch", "write_stdin", "write",
        };

        _ = await Assert.That(string.Join(",", definitions.Keys.Order(StringComparer.Ordinal)))
            .IsEqualTo(string.Join(",", expected.Order(StringComparer.Ordinal)));
        foreach (var definition in definitions.Values)
        {
            _ = await Assert.That(definition.Description).IsNotEmpty();
            using var schema = JsonDocument.Parse(definition.ParametersJson);
            _ = await Assert.That(schema.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
            _ = await Assert.That(schema.RootElement.GetProperty("type").GetString()).IsEqualTo("object");
            await AssertDescriptions(schema.RootElement);
        }

        using var question = JsonDocument.Parse(definitions["question"].ParametersJson);
        _ = await Assert.That(question.RootElement.GetProperty("properties").GetProperty("questions")
            .GetProperty("items").GetProperty("properties").GetProperty("prompt")
            .GetProperty("description").GetString()).IsNotEmpty();
        using var queueCreate = JsonDocument.Parse(definitions["queue_create"].ParametersJson);
        _ = await Assert.That(queueCreate.RootElement.GetProperty("properties").GetProperty("description")
            .GetProperty("description").GetString()).IsNotEmpty();
        using var runAgentTasks = JsonDocument.Parse(definitions["run_agent_tasks"].ParametersJson);
        var runAgentTasksSchema = runAgentTasks.RootElement;
        _ = await Assert.That(runAgentTasksSchema.GetProperty("additionalProperties").GetBoolean()).IsFalse();
        _ = await Assert.That(runAgentTasksSchema.GetProperty("oneOf").GetArrayLength()).IsEqualTo(2);
        _ = await Assert.That(runAgentTasksSchema.GetProperty("oneOf")[0].GetProperty("required")[0].GetString())
            .IsEqualTo("path");
        _ = await Assert.That(runAgentTasksSchema.GetProperty("oneOf")[1].GetProperty("required")[0].GetString())
            .IsEqualTo("artifact");
        var artifact = runAgentTasksSchema.GetProperty("$defs").GetProperty("artifact");
        _ = await Assert.That(artifact.GetProperty("additionalProperties").GetBoolean()).IsFalse();
        _ = await Assert.That(artifact.GetProperty("properties").GetProperty("schema_version").GetProperty("const").GetInt32())
            .IsEqualTo(1);
        var task = runAgentTasksSchema.GetProperty("$defs").GetProperty("task");
        _ = await Assert.That(task.GetProperty("properties").GetProperty("payload").GetProperty("oneOf")[1]
            .GetProperty("items").GetProperty("$ref").GetString()).IsEqualTo("#/$defs/task");
        using var wait = JsonDocument.Parse(definitions["wait"].ParametersJson);
        _ = await Assert.That(wait.RootElement.GetProperty("properties").GetProperty("duration_ms")
            .GetProperty("maximum").GetInt64()).IsEqualTo(4_294_967_294);
        using var edit = JsonDocument.Parse(definitions["edit"].ParametersJson);
        _ = await Assert.That(edit.RootElement.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString())).DoesNotContain("replace_all");
        _ = await Assert.That(edit.RootElement.GetProperty("properties").GetProperty("replace_all")
            .GetProperty("default").GetBoolean()).IsFalse();
    }

    private static async Task AssertDescriptions(JsonElement schema)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                _ = await Assert.That(property.Value.GetProperty("description").GetString()).IsNotEmpty();
                await AssertDescriptions(property.Value);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            await AssertDescriptions(items);
        }
    }
}
