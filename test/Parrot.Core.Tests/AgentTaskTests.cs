using System.Text.Json;
using Parrot.AgentTasks;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskTests
{
    [Test]
    public async Task Artifact_parses_nested_and_top_level_sibling_graphs_and_preserves_order()
    {
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[
              {"name":"first","description":"first description","payload":"do it","acceptance_criteria":"prove it"},
              {"name":"parent","dependencies":["first"],"description":"parent description","payload":[{"name":"child","description":"child description","payload":"child work","acceptance_criteria":"child proof"}],"acceptance_criteria":"parent proof","model":"high_llm"}
            ]}
            """);

        _ = await Assert.That(artifact.SchemaVersion).IsEqualTo(1);
        _ = await Assert.That(artifact.Tasks).Count().IsEqualTo(2);
        _ = await Assert.That(string.Join(",", artifact.Tasks.Select(task => task.Name))).IsEqualTo("first,parent");
        _ = await Assert.That(string.Join(",", artifact.Tasks[1].Dependencies)).IsEqualTo("first");
        _ = await Assert.That(artifact.Tasks[1].Payload.Tasks?[0].Name).IsEqualTo("child");
        _ = await Assert.That(artifact.Tasks[1].Model).IsEqualTo("high_llm");
        _ = await Assert.That(artifact.DisplayName).IsEqualTo("2 top-level tasks");
        _ = await Assert.That(AgentTaskParser.ParseArtifact("""{"schema_version":1,"tasks":[{"name":"single","description":"d","payload":"p","acceptance_criteria":"a"}]}""").DisplayName)
            .IsEqualTo("single");
    }

    [Test]
    [Arguments("{\"schema_version\":2,\"tasks\":[]}")]
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
        var json = $"{{\"schema_version\":1,\"tasks\":[{{\"name\":\"root\",\"description\":\"d\",\"payload\":[{string.Join(',', tasks)}],\"acceptance_criteria\":\"a\"}}]}}";
        _ = await Assert.That(() => AgentTaskParser.ParseArtifact(json)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Prepare_accepts_context_only_and_sparse_patch_preserves_omitted_values()
    {
        var preparation = AgentTaskParser.ParsePrepare("{" + "\"context\":\"preparation\",\"task_patch\":{\"model\":\"high_llm\"}}");
        var task = AgentTaskParser.ParseArtifact("""{"schema_version":1,"tasks":[{"name":"x","description":"d","payload":"p","acceptance_criteria":"a"}]}""").Tasks[0];
        var effective = EffectiveAgentTask.FromArtifact(task).Apply(preparation.TaskPatch ?? throw new InvalidOperationException());

        _ = await Assert.That(preparation.Context).IsEqualTo("preparation");
        _ = await Assert.That(effective.Description).IsEqualTo("d");
        _ = await Assert.That(effective.Payload.Instruction).IsEqualTo("p");
        _ = await Assert.That(effective.Model).IsEqualTo("high_llm");
        _ = await Assert.That(() => AgentTaskParser.ParsePrepare("{\"context\":\"x\",\"task_patch\":{\"model\":null}}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParsePrepare("{\"context\":\"x\",\"task_patch\":{}}")).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("{\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}", "work result", "Accept", null)]
    [Arguments("{\"result\":\"work result\",\"verdict\":\"reject_and_halt\",\"feedback\":\"no\"}", "work result", "RejectAndHalt", null)]
    [Arguments("{\"result\":\"work result\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"again\",\"replacement_result\":\"new context\"}", "work result", "RejectAndRetry", "new context")]
    [Arguments("{\"result\":\"work result\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":[{\"name\":\"child\",\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"a\"}]}", "work result", "RejectAndRetry", null)]
    public async Task Leaf_responses_parse_result_and_verdict(string json, string result, string kind, string? replacementResult)
    {
        var response = AgentTaskParser.ParseLeafResponse(json);

        _ = await Assert.That(response.Result).IsEqualTo(result);
        _ = await Assert.That(response.Verdict.Kind.ToString()).IsEqualTo(kind);
        _ = await Assert.That(response.Verdict.Context).IsEqualTo(replacementResult);
    }

    [Test]
    [Arguments("{\"verdict\":\"accept\",\"evidence\":\"done\"}")]
    [Arguments("{\"context\":\"   \",\"verdict\":\"accept\",\"evidence\":\"done\"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"accept\",\"evidence\":\"   \"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"accept\",\"evidence\":\"done\",\"feedback\":\"no\"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_halt\",\"feedback\":\"   \"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_halt\",\"feedback\":\"no\",\"payload\":\"forbidden\"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":[]}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"   \"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"again\",\"replacement_result\":\"   \"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"again\",\"unknown\":true}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"retry\",\"feedback\":\"fix\",\"payload\":\"again\"}")]
    public async Task Leaf_responses_reject_invalid_unions_and_fields(string json) =>
        _ = await Assert.That(() => AgentTaskParser.ParseLeafResponse(json)).Throws<ArgumentException>();

    [Test]
    public async Task Verdicts_parse_and_result_serializes_deterministically()
    {
        var accept = AgentTaskParser.ParseVerdict("{\"verdict\":\"accept\",\"evidence\":\"done\"}");
        var reject = AgentTaskParser.ParseVerdict("{\"verdict\":\"reject_and_halt\",\"feedback\":\"no\"}");
        var retry = AgentTaskParser.ParseVerdict("{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"again\",\"context\":\"replacement preparation\"}");
        var serialized = new AgentTaskGraphResult(AgentTaskExecutionStatus.Failed, [
            new AgentTaskResult("a", AgentTaskExecutionStatus.Succeeded, 1, "context", "result", null, "work", accept, ["fixed"], null, null, null),
            new AgentTaskResult("b", AgentTaskExecutionStatus.Failed, 2, null, null, null, null, reject, null, null, null, null),
            new AgentTaskResult("c", AgentTaskExecutionStatus.Blocked, 0, null, null, null, null, null, null, "blocked", ["b"], null),
        ]).Serialize();
        using var document = JsonDocument.Parse(serialized);

        _ = await Assert.That((retry.Payload ?? throw new InvalidOperationException()).Instruction).IsEqualTo("again");
        _ = await Assert.That(retry.Context).IsEqualTo("replacement preparation");
        _ = await Assert.That(document.RootElement.GetProperty("status").GetString()).IsEqualTo("failed");
        _ = await Assert.That(document.RootElement.GetProperty("tasks")[0].GetProperty("name").GetString()).IsEqualTo("a");
        _ = await Assert.That(document.RootElement.GetProperty("tasks")[1].GetProperty("verdict").GetString()).IsEqualTo("reject_and_halt");
        var retryResult = new AgentTaskGraphResult(AgentTaskExecutionStatus.Failed, [
            new AgentTaskResult("retry", AgentTaskExecutionStatus.Failed, 1, null, null, null, null, retry, null, "exhausted", null, null),
        ]).Serialize();
        using var retryDocument = JsonDocument.Parse(retryResult);
        _ = await Assert.That(retryDocument.RootElement.GetProperty("tasks")[0].GetProperty("verdict").GetString()).IsEqualTo("reject_and_retry");
        _ = await Assert.That(document.RootElement.GetProperty("tasks")[0].GetProperty("retry_feedback")[0].GetString()).IsEqualTo("fixed");
        _ = await Assert.That(document.RootElement.GetProperty("tasks")[2].GetProperty("blocked_by")[0].GetString()).IsEqualTo("b");
        var patch = new AgentTaskPatch(null, AgentTaskPayload.FromInstruction("replacement"), null, OptionalValue<string>.Unspecified);
        var patchResult = new AgentTaskGraphResult(AgentTaskExecutionStatus.Succeeded, [
            new AgentTaskResult("patched", AgentTaskExecutionStatus.Succeeded, 1, null, null, patch, null, null, null, null, null, null),
        ]).Serialize();
        using var patchDocument = JsonDocument.Parse(patchResult);
        var patchJson = patchDocument.RootElement.GetProperty("tasks")[0].GetProperty("task_patch");
        _ = await Assert.That(patchJson.GetProperty("payload").GetString()).IsEqualTo("replacement");
        _ = await Assert.That(patchJson.TryGetProperty("description", out _)).IsFalse();
        _ = await Assert.That(patchJson.TryGetProperty("model", out _)).IsFalse();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"retry\",\"feedback\":\"fix\",\"payload\":\"again\"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"reject\",\"feedback\":\"fix\"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"reject_and_halt\",\"verdict\":\"accept\",\"feedback\":\"fix\",\"evidence\":\"done\"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"again\",\"context\":\"   \"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"accept\",\"evidence\":\"done\",\"context\":\"forbidden\"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"reject_and_halt\",\"feedback\":\"no\",\"context\":\"forbidden\"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"accept\",\"evidence\":\"done\",\"feedback\":\"forbidden\"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"reject_and_halt\",\"feedback\":\"no\",\"payload\":\"forbidden\"}")).Throws<ArgumentException>();
        _ = await Assert.That(() => AgentTaskParser.ParseVerdict("{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"again\",\"evidence\":\"forbidden\"}")).Throws<ArgumentException>();
    }
}
