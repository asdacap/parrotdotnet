using System.Text.Json;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ToolInputDescriptorTests
{
    [Test]
    public async Task Every_tool_input_descriptor_is_a_described_object_schema()
    {
        var descriptors = new (string Name, string Descriptor, bool OmitsAdditionalProperties)[]
        {
            ("agent_send", AgentSendTool.Input.Descriptor, false),
            ("agent_spawn", AgentSpawnTool.Input.Descriptor, false),
            ("edit", EditTool.Input.Descriptor, false),
            ("exec_command", ExecCommandTool.Input.Descriptor, false),
            ("glob", GlobTool.Input.Descriptor, false),
            ("grep", GrepTool.Input.Descriptor, false),
            ("interrupt_process", InterruptProcessTool.Input.Descriptor, true),
            ("question", QuestionTool.Input.Descriptor, false),
            ("queue_create", QueueCreateTool.Input.Descriptor, false),
            ("queue_info", QueueInfoTool.Input.Descriptor, false),
            ("queue_listen", QueueListenTool.Input.Descriptor, false),
            ("queue_push", QueuePushTool.Input.Descriptor, false),
            ("queue_take", QueueTakeTool.Input.Descriptor, false),
            ("read", ReadTool.Input.Descriptor, false),
            ("read_image", ReadImageTool.Input.Descriptor, false),
            ("status", StatusTool.Input.Descriptor, false),
            ("todoread", TodoReadTool.Input.Descriptor, false),
            ("todowrite", TodoWriteTool.Input.Descriptor, false),
            ("wait_agent", WaitAgentTool.Input.Descriptor, false),
            ("wait_process", WaitProcessTool.Input.Descriptor, true),
            ("web_fetch", WebFetchTool.Input.Descriptor, false),
            ("write_stdin", WriteStdinTool.Input.Descriptor, false),
            ("write", WriteTool.Input.Descriptor, false),
        };

        _ = await Assert.That(descriptors.Length).IsEqualTo(23);
        _ = await Assert.That(string.Join(",", descriptors.Select(descriptor => descriptor.Name)))
            .IsEqualTo("agent_send,agent_spawn,edit,exec_command,glob,grep,interrupt_process,question,queue_create,queue_info,queue_listen,queue_push,queue_take,read,read_image,status,todoread,todowrite,wait_agent,wait_process,web_fetch,write_stdin,write");

        using (var agentSpawn = JsonDocument.Parse(AgentSpawnTool.Input.Descriptor))
        {
            var root = agentSpawn.RootElement;
            var scope = root.GetProperty("properties").GetProperty("scope");
            _ = await Assert.That(scope.GetProperty("type").GetString()).IsEqualTo("string");
            _ = await Assert.That(scope.GetProperty("description").GetString()).IsNotEmpty();
            _ = await Assert.That(root.GetProperty("required").EnumerateArray()
                .Select(item => item.GetString()).Contains("scope")).IsFalse();
        }

        using (var execCommand = JsonDocument.Parse(ExecCommandTool.Input.Descriptor))
        {
            var terminal = execCommand.RootElement.GetProperty("properties").GetProperty("tty");
            _ = await Assert.That(terminal.GetProperty("type").GetString()).IsEqualTo("boolean");
            _ = await Assert.That(terminal.GetProperty("default").GetBoolean()).IsFalse();
        }

        using (var grep = JsonDocument.Parse(GrepTool.Input.Descriptor))
        {
            var include = grep.RootElement.GetProperty("properties").GetProperty("include");
            _ = await Assert.That(include.GetProperty("type").GetString()).IsEqualTo("string");
        }

        using (var interruptProcess = JsonDocument.Parse(InterruptProcessTool.Input.Descriptor))
        {
            var root = interruptProcess.RootElement;
            var properties = root.GetProperty("properties");
            _ = await Assert.That(string.Join(",", properties.EnumerateObject().Select(property => property.Name)))
                .IsEqualTo("name,signal");
            _ = await Assert.That(string.Join(",", root.GetProperty("required").EnumerateArray()
                .Select(item => item.GetString())))
                .IsEqualTo("name");
            var signal = properties.GetProperty("signal");
            _ = await Assert.That(signal.GetProperty("type").GetString()).IsEqualTo("integer");
            _ = await Assert.That(signal.GetProperty("minimum").GetInt64()).IsEqualTo(1);
            _ = await Assert.That(signal.GetProperty("maximum").GetInt64()).IsEqualTo(64);
            _ = await Assert.That(signal.GetProperty("default").GetInt64()).IsEqualTo(2);
        }

        using (var writeStdin = JsonDocument.Parse(WriteStdinTool.Input.Descriptor))
        {
            var root = writeStdin.RootElement;
            var properties = root.GetProperty("properties");
            _ = await Assert.That(string.Join(",", properties.EnumerateObject().Select(property => property.Name)))
                .IsEqualTo("name,input,yield_after_ms");
            _ = await Assert.That(string.Join(",", root.GetProperty("required").EnumerateArray()
                .Select(item => item.GetString())))
                .IsEqualTo("name,input");
            var yieldAfter = properties.GetProperty("yield_after_ms");
            _ = await Assert.That(yieldAfter.GetProperty("minimum").GetInt64()).IsEqualTo(0);
            _ = await Assert.That(yieldAfter.GetProperty("default").GetInt64()).IsEqualTo(250);
        }

        foreach (var (_, descriptor, omitsAdditionalProperties) in descriptors)
        {
            using var document = JsonDocument.Parse(descriptor);
            var root = document.RootElement;
            _ = await Assert.That(root.GetProperty("type").GetString()).IsEqualTo("object");
            _ = await Assert.That(
                !root.TryGetProperty("properties", out var properties) || properties.ValueKind == JsonValueKind.Object)
                .IsTrue();
            _ = await Assert.That(root.TryGetProperty("additionalProperties", out var additionalProperties))
                .IsEqualTo(!omitsAdditionalProperties);

            if (!omitsAdditionalProperties)
            {
                _ = await Assert.That(additionalProperties.GetBoolean()).IsFalse();
            }

            await AssertPropertyDescriptions(root);
        }
    }

    private static async Task AssertPropertyDescriptions(JsonElement schema)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                _ = await Assert.That(property.Value.GetProperty("description").GetString()).IsNotEmpty();
                await AssertPropertyDescriptions(property.Value);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            await AssertPropertyDescriptions(items);
        }
    }
}
