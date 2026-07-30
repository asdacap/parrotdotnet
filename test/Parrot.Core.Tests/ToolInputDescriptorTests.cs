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
            ("agent_send", AgentSendToolInput.Descriptor, false),
            ("agent_spawn", AgentSpawnToolInput.Descriptor, false),
            ("edit", EditToolInput.Descriptor, false),
            ("exec_command", ExecCommandToolInput.Descriptor, false),
            ("glob", GlobToolInput.Descriptor, false),
            ("grep", GrepToolInput.Descriptor, false),
            ("interrupt_process", InterruptProcessToolInput.Descriptor, true),
            ("question", QuestionToolInput.Descriptor, false),
            ("queue_create", QueueCreateToolInput.Descriptor, false),
            ("queue_info", QueueInfoToolInput.Descriptor, false),
            ("queue_listen", QueueListenToolInput.Descriptor, false),
            ("queue_push", QueuePushToolInput.Descriptor, false),
            ("queue_take", QueueTakeToolInput.Descriptor, false),
            ("read", ReadToolInput.Descriptor, false),
            ("status", StatusToolInput.Descriptor, false),
            ("todoread", TodoReadInput.Descriptor, false),
            ("todowrite", TodoWriteInput.Descriptor, false),
            ("wait_agent", WaitAgentToolInput.Descriptor, false),
            ("wait_process", WaitProcessToolInput.Descriptor, true),
            ("web_fetch", WebFetchToolInput.Descriptor, false),
            ("write", WriteToolInput.Descriptor, false),
        };

        _ = await Assert.That(descriptors.Length).IsEqualTo(21);
        _ = await Assert.That(string.Join(",", descriptors.Select(descriptor => descriptor.Name)))
            .IsEqualTo("agent_send,agent_spawn,edit,exec_command,glob,grep,interrupt_process,question,queue_create,queue_info,queue_listen,queue_push,queue_take,read,status,todoread,todowrite,wait_agent,wait_process,web_fetch,write");

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
