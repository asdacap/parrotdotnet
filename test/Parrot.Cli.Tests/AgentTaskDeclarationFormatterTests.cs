using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class AgentTaskDeclarationFormatterTests
{
    [Test]
    public async Task Format_renders_only_names_and_descriptions_in_declaration_order()
    {
        var declarations = new[]
        {
            new PlanTaskDeclaration
            {
                Name = "first",
                Status = AgentTaskProgressStatus.Pending,
                Description = "First description",
                Instruction = "First instruction",
                AcceptanceCriteria = "First criteria",
                Model = "low_llm",
            },
            new PlanTaskDeclaration
            {
                Name = "second",
                Status = AgentTaskProgressStatus.Pending,
                Dependencies = { "first" },
                Description = "Second description",
                Instruction = "Second instruction",
                AcceptanceCriteria = "Second criteria",
                Model = "provider/model",
            },
            new PlanTaskDeclaration
            {
                Name = "composite",
                Status = AgentTaskProgressStatus.Succeeded,
                Dependencies = { "first", "second" },
                Description = "Composite description",
                Instruction = "Composite instruction",
                AcceptanceCriteria = "Composite criteria",
                Model = "composite-model",
                Children = new PlanTaskDeclarationChildren
                {
                    Tasks =
                    {
                        new PlanTaskDeclaration
                        {
                            Name = "child",
                            Status = AgentTaskProgressStatus.Pending,
                            Dependencies = { "external" },
                            Description = "Child description",
                            Instruction = "Child instruction",
                            AcceptanceCriteria = "Child criteria",
                            Model = "child-model",
                        },
                        new PlanTaskDeclaration
                        {
                            Name = "last child",
                            Status = AgentTaskProgressStatus.Pending,
                            Description = "Last child description",
                            Instruction = "Last child instruction",
                            AcceptanceCriteria = "Last child criteria",
                        },
                    },
                },
            },
        };

        var rendered = string.Join('\n', AgentTaskDeclarationFormatter.Format(declarations));

        _ = await Assert.That(rendered).IsEqualTo(
            "Agent tasks:\n" +
            "- name: first\n" +
            "  description: First description\n" +
            "- name: second\n" +
            "  description: Second description\n" +
            "- name: composite\n" +
            "  description: Composite description\n" +
            "    - name: child\n" +
            "      description: Child description\n" +
            "    - name: last child\n" +
            "      description: Last child description");

        _ = await Assert.That(rendered).DoesNotContain("status:");
        _ = await Assert.That(rendered).DoesNotContain("dependencies:");
        _ = await Assert.That(rendered).DoesNotContain("payload:");
        _ = await Assert.That(rendered).DoesNotContain("instruction");
        _ = await Assert.That(rendered).DoesNotContain("acceptance_criteria:");
        _ = await Assert.That(rendered).DoesNotContain("model:");
        _ = await Assert.That(rendered).DoesNotContain("First instruction");
        _ = await Assert.That(rendered).DoesNotContain("Composite criteria");
        _ = await Assert.That(rendered).DoesNotContain("child-model");
    }

    [Test]
    public async Task Format_sanitizes_names_and_multiline_descriptions_without_block_markers()
    {
        var declarations = new[]
        {
            new PlanTaskDeclaration
            {
                Name = "task\u001b[2J\nname\tvalue",
                Status = AgentTaskProgressStatus.Pending,
                Dependencies = { "dependency\nname" },
                Description = "first line\nsecond\tline\u001b[H",
                Instruction = "instruction",
                AcceptanceCriteria = "criteria",
                Model = "model",
            },
        };

        var rendered = string.Join('\n', AgentTaskDeclarationFormatter.Format(declarations));

        _ = await Assert.That(rendered).IsEqualTo(
            "Agent tasks:\n" +
            "- name: task[2J name    value\n" +
            "  description:\n" +
            "    first line\n" +
            "    second    line[H");
        _ = await Assert.That(rendered).DoesNotContain('\u001b');
        _ = await Assert.That(rendered).DoesNotContain("|-", StringComparison.Ordinal);
        _ = await Assert.That(rendered).DoesNotContain("dependency");
        _ = await Assert.That(rendered).DoesNotContain("instruction");
        _ = await Assert.That(rendered).DoesNotContain("criteria");
        _ = await Assert.That(rendered).DoesNotContain("model");
    }

    [Test]
    public async Task Format_for_width_wraps_unicode_names_and_descriptions_with_hanging_indentation()
    {
        var declarations = new[]
        {
            new PlanTaskDeclaration
            {
                Name = "日本語-task",
                Status = AgentTaskProgressStatus.Pending,
                Description = "説明文 words\nsecond line",
                Instruction = "instruction words",
                AcceptanceCriteria = "criteria words",
                Model = "narrow-model",
            },
        };

        var rendered = AgentTaskDeclarationFormatter.FormatForWidth(declarations, 16);

        _ = await Assert.That(rendered[0]).IsEqualTo("Agent tasks:");
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain("instruction");
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain("criteria");
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain("narrow-model");
        var wrapped = string.Join('\n', rendered);
        var compacted = string.Concat(rendered).Replace(" ", string.Empty, StringComparison.Ordinal);
        _ = await Assert.That(compacted).Contains("日本語-task");
        _ = await Assert.That(wrapped).Contains("説明文");
        _ = await Assert.That(wrapped).Contains("second line");
        _ = await Assert.That(rendered.Count).IsGreaterThan(5);

        foreach (var line in rendered.Skip(1))
        {
            _ = await Assert.That(Parrot.Cli.Enhanced.TerminalText.Width(line)).IsLessThanOrEqualTo(16);
        }

        var descriptionLine = rendered.ToList().IndexOf("  description:");
        _ = await Assert.That(descriptionLine).IsGreaterThan(0);
        _ = await Assert.That(rendered[descriptionLine + 1]).StartsWith("    ");
        _ = await Assert.That(rendered[descriptionLine + 2]).StartsWith("    ");
    }
}
