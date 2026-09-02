using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class AgentTaskDeclarationFormatterTests
{
    [Test]
    public async Task Format_renders_complete_nested_declarations_and_omits_only_previous_sibling_dependency()
    {
        var declarations = new[]
        {
            Leaf("first", [], "First description", "First instruction", "First criteria", "low_llm"),
            Leaf("second", ["first"], "Second description", "Second instruction", "Second criteria", null),
            new PlanTaskDeclaration
            {
                Name = "composite",
                Status = AgentTaskProgressStatus.Pending,
                Dependencies = { "first", "second" },
                Description = "Composite description",
                AcceptanceCriteria = "Composite criteria",
                Children = new PlanTaskDeclarationChildren
                {
                    Tasks =
                    {
                        Leaf("child", ["external"], "Child description", "Child instruction", "Child criteria", null),
                    },
                },
            },
        };

        var rendered = string.Join('\n', AgentTaskDeclarationFormatter.Format(declarations));

        _ = await Assert.That(rendered).IsEqualTo(
            "Agent tasks:\n" +
            "- name: first\n" +
            "  status: pending\n" +
            "  description: First description\n" +
            "  payload: First instruction\n" +
            "  acceptance_criteria: First criteria\n" +
            "  model: low_llm\n" +
            "- name: second\n" +
            "  status: pending\n" +
            "  description: Second description\n" +
            "  payload: Second instruction\n" +
            "  acceptance_criteria: Second criteria\n" +
            "- name: composite\n" +
            "  status: pending\n" +
            "  dependencies: [first, second]\n" +
            "  description: Composite description\n" +
            "  payload:\n" +
            "    - name: child\n" +
            "      status: pending\n" +
            "      dependencies: [external]\n" +
            "      description: Child description\n" +
            "      payload: Child instruction\n" +
            "      acceptance_criteria: Child criteria\n" +
            "  acceptance_criteria: Composite criteria");
    }

    [Test]
    public async Task Format_sanitizes_and_indents_multiline_values_without_block_markers()
    {
        var declarations = new[]
        {
            Leaf(
                "task\u001b[2J",
                ["dependency\nname"],
                "first line\nsecond\tline",
                new string('x', 81),
                "criteria",
                "model\u001b[H"),
        };

        var rendered = string.Join('\n', AgentTaskDeclarationFormatter.Format(declarations));

        _ = await Assert.That(rendered).Contains("- name: task[2J");
        _ = await Assert.That(rendered).Contains("dependencies: [dependency name]");
        _ = await Assert.That(rendered).Contains("  description:\n    first line\n    second    line");
        _ = await Assert.That(rendered).Contains("  payload:\n    " + new string('x', 81));
        _ = await Assert.That(rendered).Contains("model: model[H");
        _ = await Assert.That(rendered).DoesNotContain("\u001b");
        _ = await Assert.That(rendered).DoesNotContain("|-");
    }

    [Test]
    public async Task Format_for_width_wraps_without_omitting_content()
    {
        var declarations = new[]
        {
            Leaf("日本語-task", [], "description words", "instruction words", "criteria words", null),
        };

        var rendered = AgentTaskDeclarationFormatter.FormatForWidth(declarations, 16);

        var compacted = string.Concat(rendered).Replace(" ", string.Empty, StringComparison.Ordinal);
        _ = await Assert.That(rendered.Count).IsGreaterThan(7);
        _ = await Assert.That(compacted).Contains("日本語-task");
        _ = await Assert.That(compacted).Contains("instructionwords");
        _ = await Assert.That(compacted).Contains("criteriawords");
    }

    private static PlanTaskDeclaration Leaf(
        string name,
        IReadOnlyList<string> dependencies,
        string description,
        string instruction,
        string acceptanceCriteria,
        string? model)
    {
        var declaration = new PlanTaskDeclaration
        {
            Name = name,
            Status = AgentTaskProgressStatus.Pending,
            Description = description,
            Instruction = instruction,
            AcceptanceCriteria = acceptanceCriteria,
        };
        declaration.Dependencies.Add(dependencies);
        if (model is not null)
        {
            declaration.Model = model;
        }

        return declaration;
    }
}
