using Parrot.Config;
using YamlDotNet.RepresentationModel;

namespace Parrot.Core.Tests;

internal sealed class PredefinedConfigurationComposerTests
{
    [Test]
    public async Task Disjoint_settings_and_template_ids_combine_deterministically()
    {
        (string Name, string? Content)[] sources =
        [
            ("prompts", """
                system_prompts:
                  base: |-
                    First {literal}
                    Second line
                prompt_templates:
                  ordinary:
                    template: >-
                      Hello
                      {name}
                    allowed_arguments: [name]
                """),
            ("tasks", """
                system_prompts:
                  agent-tasks: Task guidance
                tools:
                  task-tool:
                    description: Task tool
                agent_tasks:
                  maximum_attempts: 2
                prompt_templates:
                  agent-task.custom:
                    template: '{result}'
                """),
            ("runtime", """
                empty: {}
                tools:
                  sample:
                    parameters:
                      type: object
                      properties:
                        items:
                          type: array
                          items:
                            type: string
                      required: [items]
                ordered: [third, first, second]
                """)
        ];
        var content = PredefinedConfigurationComposer.Compose(sources);
        _ = await Assert.That(content).IsEqualTo(PredefinedConfigurationComposer.Compose(sources));
        var stream = new YamlStream();
        using var reader = new StringReader(content);
        stream.Load(reader);
        _ = await Assert.That(stream.Documents.Count).IsEqualTo(1);
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        _ = await Assert.That(root.Children.Count).IsEqualTo(6);
        _ = await Assert.That(root["system_prompts"]["base"].ToString()).IsEqualTo("First {literal}\nSecond line");
        _ = await Assert.That(root["system_prompts"]["agent-tasks"].ToString()).IsEqualTo("Task guidance");
        _ = await Assert.That(root["tools"]["task-tool"]["description"].ToString()).IsEqualTo("Task tool");
        var templates = (YamlMappingNode)root["prompt_templates"];
        _ = await Assert.That(templates.Children.Count).IsEqualTo(2);
        _ = await Assert.That(templates["ordinary"]["template"].ToString()).IsEqualTo("Hello {name}");
        _ = await Assert.That(templates["agent-task.custom"]["template"].ToString()).IsEqualTo("{result}");
        _ = await Assert.That(((YamlMappingNode)root["empty"]).Children.Count).IsEqualTo(0);
        _ = await Assert.That(root["tools"]["sample"]["parameters"]["properties"]["items"]["items"]["type"].ToString())
            .IsEqualTo("string");
        _ = await Assert.That(((YamlSequenceNode)root["ordered"]).Children.Select(item => item.ToString())
            .SequenceEqual(["third", "first", "second"], StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments("model: first", "model: first", "model")]
    [Arguments("model: first", "model: second", "model")]
    [Arguments("system_prompts:\n  shared: first", "system_prompts:\n  shared: second", "system_prompts.shared")]
    [Arguments("tools:\n  shared:\n    description: first", "tools:\n  shared:\n    description: second", "tools.shared")]
    [Arguments(
        "prompt_templates:\n  shared:\n    template: first",
        "prompt_templates:\n  shared:\n    template: first",
        "prompt_templates.shared")]
    [Arguments(
        "prompt_templates:\n  shared:\n    template: first",
        "prompt_templates:\n  shared:\n    allowed_arguments: []",
        "prompt_templates.shared")]
    public async Task Duplicate_definitions_report_both_sources_and_path(string first, string second, string path)
    {
        var exception = Assert.Throws<InvalidDataException>(() => PredefinedConfigurationComposer.Compose(
            [("first.yaml", first), ("second.yaml", second)]));

        _ = await Assert.That(exception.Message).Contains("duplicate predefined configuration");
        _ = await Assert.That(exception.Message).Contains("first.yaml");
        _ = await Assert.That(exception.Message).Contains("second.yaml");
        _ = await Assert.That(exception.Message).Contains(path);
    }

    [Test]
    [Arguments("model: [", "invalid predefined configuration YAML")]
    [Arguments("model: one\n---\nmodel: two", "exactly one mapping document")]
    [Arguments("- item", "exactly one mapping document")]
    [Arguments("scalar", "exactly one mapping document")]
    [Arguments("", "exactly one mapping document")]
    [Arguments("prompt_templates: []", "prompt_templates must be a mapping")]
    [Arguments("system_prompts: []", "system_prompts must be a mapping")]
    [Arguments("tools: []", "tools must be a mapping")]
    [Arguments("tools:\n  ? [one, two]\n  : value", "mapping keys at tools must be strings")]
    public async Task Invalid_sources_report_resource_and_reason(string content, string reason)
    {
        var exception = Assert.Throws<InvalidDataException>(() => PredefinedConfigurationComposer.Compose(
            [("invalid.yaml", content)]));

        _ = await Assert.That(exception.Message).Contains("invalid.yaml");
        _ = await Assert.That(exception.Message).Contains(reason);
    }

    [Test]
    public async Task Missing_resource_reports_its_name()
    {
        var exception = Assert.Throws<InvalidDataException>(() => PredefinedConfigurationComposer.Compose(
            [("present.yaml", "model: sample"), ("missing.yaml", null)]));

        _ = await Assert.That(exception.Message).Contains("missing.yaml");
        _ = await Assert.That(exception.Message).Contains("was not embedded in this build");
    }
}
