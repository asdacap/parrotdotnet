using System.Text.Json;
using Parrot.AgentTasks;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskTests
{
    [Test]
    public async Task Artifact_parses_nested_sibling_graphs_and_preserves_order()
    {
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[
              {"name":"first","description":"first description","payload":"do it","acceptance_criteria":"prove it"},
              {"name":"parent","dependencies":["first"],"description":"parent description","payload":[{"name":"child","description":"child description","payload":"child work","acceptance_criteria":"child proof"}],"acceptance_criteria":"parent proof","model":"high_llm"}
            ]}
            """);

        _ = await Assert.That(artifact.SchemaVersion).IsEqualTo(1);
        _ = await Assert.That(string.Join(",", artifact.Tasks.Select(task => task.Name))).IsEqualTo("first,parent");
        _ = await Assert.That(string.Join(",", artifact.Tasks[1].Dependencies)).IsEqualTo("first");
        _ = await Assert.That(artifact.Tasks[1].Payload.Tasks?[0].Name).IsEqualTo("child");
        _ = await Assert.That(artifact.Tasks[1].Model).IsEqualTo("high_llm");
    }

    [Test]
    [Arguments("{\"schema_version\":2,\"tasks\":[]}")]
    [Arguments("{\"schema_version\":1,\"tasks\":[]}")]
    [Arguments("{\"schema_version\":1,\"tasks\":null}")]
    [Arguments("{\"schema_version\":1,\"tasks\":[],\"unknown\":true}")]
    [Arguments("{\"schema_version\":1,\"tasks\":[{\"name\":\"x\",\"description\":\"d\",\"payload\":null,\"acceptance_criteria\":\"a\"}]}")]
    public async Task Artifact_rejects_invalid_envelopes_and_payload_unions(string json) =>
        _ = await Assert.That(() => AgentTaskParser.ParseArtifact(json)).Throws<ArgumentException>();

    [Test]
    [Arguments("a,a", "")]
    [Arguments("a", "missing")]
    [Arguments("a", "a")]
    [Arguments("a,b", "b,a")]
    public async Task Artifact_rejects_invalid_sibling_dependencies(string names, string dependencies)
    {
        var taskNames = names.Split(',');
        var tasks = taskNames.Select((name, index) =>
            $"{{\"name\":\"{name}\",\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"a\"{(index == taskNames.Length - 1 && dependencies.Length > 0 ? $",\"dependencies\":[{string.Join(',', dependencies.Split(',').Select(value => $"\"{value}\""))}]" : string.Empty)}}}");
        var json = $"{{\"schema_version\":1,\"tasks\":[{string.Join(',', tasks)}]}}";
        _ = await Assert.That(() => AgentTaskParser.ParseArtifact(json)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Hook_accepts_context_only_and_sparse_patch_preserves_omitted_values()
    {
        var hook = AgentTaskParser.ParseResearchHook("{" + "\"context\":\"research\",\"task_patch\":{\"model\":\"high_llm\"}}");
        var task = AgentTaskParser.ParseArtifact("""{"schema_version":1,"tasks":[{"name":"x","description":"d","payload":"p","acceptance_criteria":"a"}]}""").Tasks[0];
        var effective = EffectiveAgentTask.FromArtifact(task).Apply(hook.TaskPatch ?? throw new InvalidOperationException());

        _ = await Assert.That(hook.Context).IsEqualTo("research");
        _ = await Assert.That(effective.Description).IsEqualTo("d");
        _ = await Assert.That(effective.Payload.Instruction).IsEqualTo("p");
        _ = await Assert.That(effective.Model).IsEqualTo("high_llm");
        _ = await Assert.That(() => AgentTaskParser.ParseResearchHook("{\"context\":\"x\",\"task_patch\":{\"model\":null}}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseResearchHook("{\"context\":\"x\",\"task_patch\":{}}")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Verdicts_parse_and_result_serializes_deterministically()
    {
        var accept = AgentTaskParser.ParseVerdict("{\"verdict\":\"accept\",\"evidence\":\"done\"}");
        var reject = AgentTaskParser.ParseVerdict("{\"verdict\":\"reject\",\"feedback\":\"no\"}");
        var retry = AgentTaskParser.ParseVerdict("{\"verdict\":\"retry\",\"feedback\":\"fix\",\"payload\":\"again\"}");
        var serialized = new AgentTaskGraphResult(AgentTaskExecutionStatus.Failed, [
            new AgentTaskResult("a", AgentTaskExecutionStatus.Succeeded, 1, "context", null, "work", accept, ["fixed"], null, null, null),
            new AgentTaskResult("b", AgentTaskExecutionStatus.Failed, 2, null, null, null, reject, null, null, null, null),
            new AgentTaskResult("c", AgentTaskExecutionStatus.Blocked, 0, null, null, null, null, null, "blocked", ["b"], null),
        ]).Serialize();
        using var document = JsonDocument.Parse(serialized);

        _ = await Assert.That((retry.Payload ?? throw new InvalidOperationException()).Instruction).IsEqualTo("again");
        _ = await Assert.That(document.RootElement.GetProperty("status").GetString()).IsEqualTo("failed");
        _ = await Assert.That(document.RootElement.GetProperty("tasks")[0].GetProperty("name").GetString()).IsEqualTo("a");
        _ = await Assert.That(document.RootElement.GetProperty("tasks")[0].GetProperty("retry_feedback")[0].GetString()).IsEqualTo("fixed");
        _ = await Assert.That(document.RootElement.GetProperty("tasks")[2].GetProperty("blocked_by")[0].GetString()).IsEqualTo("b");
        var patch = new AgentTaskPatch(null, AgentTaskPayload.FromInstruction("replacement"), null, OptionalValue<string>.Unspecified);
        var patchResult = new AgentTaskGraphResult(AgentTaskExecutionStatus.Succeeded, [
            new AgentTaskResult("patched", AgentTaskExecutionStatus.Succeeded, 1, null, patch, null, null, null, null, null, null),
        ]).Serialize();
        using var patchDocument = JsonDocument.Parse(patchResult);
        var patchJson = patchDocument.RootElement.GetProperty("tasks")[0].GetProperty("task_patch");
        _ = await Assert.That(patchJson.GetProperty("payload").GetString()).IsEqualTo("replacement");
        _ = await Assert.That(patchJson.TryGetProperty("description", out _)).IsFalse();
        _ = await Assert.That(patchJson.TryGetProperty("model", out _)).IsFalse();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"retry\",\"feedback\":\"fix\"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"accept\",\"evidence\":\"done\",\"feedback\":\"forbidden\"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"reject\",\"feedback\":\"no\",\"payload\":\"forbidden\"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"retry\",\"feedback\":\"fix\",\"payload\":\"again\",\"evidence\":\"forbidden\"}")).Throws<ArgumentException>();
    }
}
