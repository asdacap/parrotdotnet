using Parrot.AgentTasks;
using Parrot.Protocol;

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
    public async Task Hidden_defaults_to_false_and_parses_recursively_from_artifacts_and_task_sets()
    {
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"visible","description":"d","payload":[{"name":"hidden-child","description":"d","payload":"p","acceptance_criteria":"a","hidden":true}],"acceptance_criteria":"a"},{"name":"hidden","description":"d","payload":"p","acceptance_criteria":"a","hidden":true}]}
            """);
        var tasks = AgentTaskParser.ParseTaskSet("""
            [{"name":"visible","description":"d","payload":"p","acceptance_criteria":"a"},{"name":"shown","description":"d","payload":"p","acceptance_criteria":"a","hidden":false}]
            """);

        _ = await Assert.That(artifact.Tasks[0].Hidden).IsFalse();
        var children = artifact.Tasks[0].Payload.Tasks ?? throw new InvalidOperationException("The child declarations are missing.");
        _ = await Assert.That(children[0].Hidden).IsTrue();
        _ = await Assert.That(artifact.Tasks[1].Hidden).IsTrue();
        _ = await Assert.That(tasks[0].Hidden).IsFalse();
        _ = await Assert.That(tasks[1].Hidden).IsFalse();
    }

    [Test]
    [Arguments("null")]
    [Arguments("\"true\"")]
    [Arguments("1")]
    [Arguments("[]")]
    [Arguments("{}")]
    public async Task Hidden_rejects_non_boolean_values_recursively(string value)
    {
        var json = $$"""{"schema_version":1,"tasks":[{"name":"root","description":"d","payload":[{"name":"child","description":"d","payload":"p","acceptance_criteria":"a","hidden":{{value}}}],"acceptance_criteria":"a"}]}""";

        _ = await Assert.That(() => AgentTaskParser.ParseArtifact(json)).Throws<ArgumentException>()
            .WithMessage("tasks[0] payload[0] hidden must be a boolean.");
        var taskSet = $$"""[{"name":"root","description":"d","payload":"p","acceptance_criteria":"a","hidden":{{value}}}]""";
        _ = await Assert.That(() => AgentTaskParser.ParseTaskSet(taskSet)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Planned_task_conversions_preserve_hidden_recursively()
    {
        var tasks = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"root","description":"d","payload":[{"name":"child","description":"d","payload":"p","acceptance_criteria":"a","hidden":true}],"acceptance_criteria":"a","hidden":true}]}
            """).Tasks;

        var progress = AgentTaskProgressSnapshot.FromPlannedTasks(tasks);
        var declarations = PlanTaskDeclaration.FromPlannedTasks(tasks);

        _ = await Assert.That(progress.RootNodes[0].Hidden).IsTrue();
        _ = await Assert.That(progress.RootNodes[0].Children[0].Hidden).IsTrue();
        _ = await Assert.That(declarations[0].Hidden).IsTrue();
        _ = await Assert.That(declarations[0].Children.Tasks[0].Hidden).IsTrue();
    }

    [Test]
    public async Task Legacy_protobuf_task_messages_default_to_visible()
    {
        byte[] legacy = [10, 4, 116, 97, 115, 107];
        var progress = AgentTaskProgressNode.Parser.ParseFrom(legacy);
        var declaration = PlanTaskDeclaration.Parser.ParseFrom(legacy);

        _ = await Assert.That(progress.Name).IsEqualTo("task");
        _ = await Assert.That(progress.Hidden).IsFalse();
        _ = await Assert.That(declaration.Name).IsEqualTo("task");
        _ = await Assert.That(declaration.Hidden).IsFalse();
    }

    [Test]
    [Arguments("{\"schema_version\":2,\"tasks\":[]}")]
    [Arguments("{\"schema_version\":1,\"tasks\":null}")]
    [Arguments("{\"schema_version\":1,\"tasks\":[],\"unknown\":true}")]
    [Arguments("{\"schema_version\":1,\"tasks\":[{\"name\":\"x\",\"description\":\"d\",\"payload\":null,\"acceptance_criteria\":\"a\"}]}")]
    public async Task Artifact_rejects_invalid_envelopes_and_payload_unions(string json) =>
        _ = await Assert.That(() => AgentTaskParser.ParseArtifact(json)).Throws<ArgumentException>();

    [Test]
    public async Task Description_is_limited_to_max_length_for_artifact_and_nested_tasks()
    {
        static string Artifact(string topDescription, string childDescription) =>
            $$"""{"schema_version":1,"tasks":[{"name":"root","description":"{{topDescription}}","payload":[{"name":"child","description":"{{childDescription}}","payload":"p","acceptance_criteria":"a"}],"acceptance_criteria":"a"}]}""";
        var atLimit = new string('d', AgentTaskParser.MaxDescriptionLength);
        var overLimit = new string('d', AgentTaskParser.MaxDescriptionLength + 1);

        _ = await Assert.That(AgentTaskParser.ParseArtifact(Artifact(atLimit, atLimit)).Tasks[0].Description).IsEqualTo(atLimit);
        _ = await Assert.That(() => AgentTaskParser.ParseArtifact(Artifact(overLimit, "d"))).Throws<ArgumentException>()
            .WithMessage($"tasks[0] description must be at most {AgentTaskParser.MaxDescriptionLength} characters.");
        _ = await Assert.That(() => AgentTaskParser.ParseArtifact(Artifact("d", overLimit))).Throws<ArgumentException>();
    }

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
    [Arguments("{\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}", "work result", "Accept", null)]
    [Arguments("{\"result\":\"work result\",\"verdict\":\"reject_and_halt\",\"feedback\":\"no\"}", "work result", "RejectAndHalt", null)]
    [Arguments("{\"result\":\"work result\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"again\",\"replacement_result\":\"new context\"}", "work result", "RejectAndRetry", "new context")]
    [Arguments("{\"result\":\"work result\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\"}", "work result", "RejectAndRetry", null)]
    public async Task Leaf_responses_parse_result_and_verdict(string json, string result, string kind, string? replacementResult)
    {
        var response = AgentTaskParser.ParseLeafResponse(json);

        _ = await Assert.That(response.Result).IsEqualTo(result);
        _ = await Assert.That(response.Verdict.Kind.ToString()).IsEqualTo(kind);
        _ = await Assert.That(response.Verdict.ReplacementResult).IsEqualTo(replacementResult);
        _ = await Assert.That(response.Verdict.ReplacementInstruction).IsEqualTo(replacementResult is null ? null : "again");
    }

    [Test]
    [Arguments("{\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}")]
    [Arguments("Here is my response:\n{\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}")]
    [Arguments("Add `foo() {` to the file.\n{\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}")]
    [Arguments("Match with regex [^\"]+ then output:\n{\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}")]
    [Arguments("```c\nprintf(\"{\");\n```\n{\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}")]
    [Arguments("Example: {\"result\":\"oops\n{\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}")]
    [Arguments("Example envelope: {\"result\":\"example\",\"verdict\":\"reject_and_halt\",\"feedback\":\"ignore me\"}\nHere is the answer: {\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}")]
    [Arguments("Sure.\n```json\n{\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}\n```\nDone.")]
    [Arguments("Sure.\n```\n{\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}\n```")]
    [Arguments("\uFEFF  {\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}  ")]
    public async Task Leaf_responses_tolerate_surrounding_prose_and_fences(string json)
    {
        var response = AgentTaskParser.ParseLeafResponse(json);

        _ = await Assert.That(response.Result).IsEqualTo("work result");
        _ = await Assert.That(response.Verdict.Kind).IsEqualTo(AcceptanceVerdictKind.Accept);
    }

    [Test]
    public async Task Leaf_responses_keep_braces_and_brackets_inside_string_values()
    {
        var response = AgentTaskParser.ParseLeafResponse(
            "Here it is:\n{\"result\":\"contains {braces} and [brackets] in the text\",\"verdict\":\"accept\",\"evidence\":\"done\"}");

        _ = await Assert.That(response.Result).IsEqualTo("contains {braces} and [brackets] in the text");
        _ = await Assert.That(response.Verdict.Kind).IsEqualTo(AcceptanceVerdictKind.Accept);
    }

    [Test]
    [Arguments("no json here at all")]
    [Arguments("I could not complete the task.")]
    [Arguments("Here is a wrapped envelope: {\"result\":\"x\",\"verdict\":\"accept\",\"evidence\":\"done\",\"unknown\":true}")]
    [Arguments("{\"result\":\"work result\",\"verdict\":\"accept\",\"evidence\":\"done\"}\nLet me know if you need anything else.")]
    public async Task Leaf_responses_still_reject_prose_without_an_envelope_trailing_prose_and_unknown_fields(string json) =>
        _ = await Assert.That(() => AgentTaskParser.ParseLeafResponse(json)).Throws<ArgumentException>();

    [Test]
    public async Task Artifacts_tolerate_surrounding_prose()
    {
        var artifact = AgentTaskParser.ParseArtifact(
            "Here is the task graph:\n```json\n{\"schema_version\":1,\"tasks\":[{\"name\":\"single\",\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"a\"}]}\n```");

        _ = await Assert.That(artifact.DisplayName).IsEqualTo("single");
    }

    [Test]
    [Arguments("{\"verdict\":\"accept\",\"evidence\":\"done\"}")]
    [Arguments("{\"context\":\"   \",\"verdict\":\"accept\",\"evidence\":\"done\"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"accept\",\"evidence\":\"   \"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"accept\",\"evidence\":\"done\",\"feedback\":\"no\"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_halt\",\"feedback\":\"   \"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_halt\",\"feedback\":\"no\",\"payload\":\"forbidden\"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":[]}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":[{\"name\":\"child\",\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"a\"}]}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"   \"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"again\",\"replacement_result\":\"   \"}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"again\",\"unknown\":true}")]
    [Arguments("{\"result\":\"x\",\"verdict\":\"retry\",\"feedback\":\"fix\",\"payload\":\"again\"}")]
    public async Task Leaf_responses_reject_invalid_unions_and_fields(string json) =>
        _ = await Assert.That(() => AgentTaskParser.ParseLeafResponse(json)).Throws<ArgumentException>();

    [Test]
    public async Task Task_set_parses_states_and_defers_dependency_validation_to_the_merged_graph()
    {
        var tasks = AgentTaskParser.ParseTaskSet("""
            [
              {"name":"build","dependencies":["declared-earlier"],"description":"d","payload":"p","acceptance_criteria":"a","state":"succeeded","result":"built"},
              {"name":"lint","description":"d","payload":"p","acceptance_criteria":"a","state":"failed","failure":"style"},
              {"name":"fresh","description":"d","payload":"p","acceptance_criteria":"a"}
            ]
            """);

        _ = await Assert.That(string.Join(",", tasks.Select(task => $"{task.Name}:{task.State}:{task.Result}:{task.Failure}")))
            .IsEqualTo("build:Succeeded:built:,lint:Failed::style,fresh:Pending::");
        _ = await Assert.That(() => AgentTaskParser.ValidateGraph(tasks, "tasks")).Throws<ArgumentException>()
            .WithMessage("tasks task 'build' has missing sibling dependency 'declared-earlier'.");
        _ = await Assert.That(AgentTaskParser.ParseArtifact("""{"schema_version":1,"tasks":[{"name":"x","description":"d","payload":"p","acceptance_criteria":"a","state":"canceled"}]}""").Tasks[0].State)
            .IsEqualTo(AgentTaskExecutionStatus.Canceled);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("[]")]
    [Arguments("[{\"name\":\"x\",\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"a\",\"agent_name\":\"worker\"}]")]
    [Arguments("[{\"name\":\"x\",\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"a\",\"state\":\"blocked\"}]")]
    [Arguments("[{\"name\":\"x\",\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"a\",\"result\":\"  \"}]")]
    [Arguments("[{\"name\":\"x\",\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"a\"},{\"name\":\"x\",\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"a\"}]")]
    [Arguments("[{\"name\":\"x\",\"dependencies\":[\"x\"],\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"a\"}]")]
    [Arguments("[{\"name\":\"x\",\"description\":\"d\",\"payload\":[{\"name\":\"child\",\"dependencies\":[\"missing\"],\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"a\"}],\"acceptance_criteria\":\"a\"}]")]
    public async Task Task_set_rejects_invalid_sets(string json) =>
        _ = await Assert.That(() => AgentTaskParser.ParseTaskSet(json)).Throws<ArgumentException>();
}
