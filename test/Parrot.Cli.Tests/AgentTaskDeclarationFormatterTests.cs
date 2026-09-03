using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class AgentTaskDeclarationFormatterTests
{
    [Test]
    public async Task Format_renders_only_names_and_descriptions_in_declaration_order()
    {
        var declarations = new[]
        {
            Leaf("first", [], "First description", "First instruction", "First criteria", "low_llm"),
            Leaf("second", ["first"], "Second description", "Second instruction", "Second criteria", "provider/model"),
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
                        Leaf("child", ["external"], "Child description", "Child instruction", "Child criteria", "child-model"),
                        Leaf("last child", [], "Last child description", "Last child instruction", "Last child criteria", null),
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
            Leaf(
                "task\u001b[2J\nname\tvalue",
                ["dependency\nname"],
                "first line\nsecond\tline\u001b[H",
                "instruction",
                "criteria",
                "model"),
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
            Leaf(
                "日本語-task",
                [],
                "説明文 words\nsecond line",
                "instruction words",
                "criteria words",
                "narrow-model"),
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
