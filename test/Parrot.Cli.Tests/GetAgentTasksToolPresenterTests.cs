using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class GetAgentTasksToolPresenterTests
{
    private const string Leaf = "{\"name\":\"task\",\"description\":\"Do work\",\"dependencies\":[],\"acceptance_criteria\":\"Work done\",\"payload\":\"Run work\",\"state\":\"succeeded\"}";
    private static readonly ScrollbackRenderContext Context = new(32_768, new TerminalPalette(false));

    [Test]
    [Arguments("{}", "Agent tasks")]
    [Arguments("{\"name\":\"task\"}", "Agent task · task")]
    public async Task Live_labels_select_query_form(string arguments, string label)
    {
        var registry = Registry();
        var lines = registry.PresentLive(new ToolCallPresentation("get_agent_tasks", arguments), 0)
            .Render(new LiveBufferRenderContext(80, new TerminalPalette(false))).Lines;

        _ = await Assert.That(lines[0].Text).IsEqualTo($"⠋ {label}");
        _ = await Assert.That(lines.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("pending", "○")]
    [Arguments("running", "◐")]
    [Arguments("succeeded", "✓")]
    [Arguments("failed", "✗")]
    [Arguments("canceled", "■")]
    public async Task Summary_displays_explicit_state_name_and_description(string state, string icon)
    {
        var result = $"[{{\"name\":\"first\",\"state\":\"{state}\",\"description\":\"First work\"}},{{\"name\":\"second\",\"state\":\"pending\",\"description\":\"Second work\"}}]";
        var text = string.Join('\n', Render("{}", result));

        _ = await Assert.That(text).IsEqualTo($"✓ Agent tasks\n  {icon} {state} · first · First work\n  ○ pending · second · Second work");
    }

    [Test]
    public async Task Empty_list_is_explicit()
    {
        _ = await Assert.That(string.Join('\n', Render("{}", "[]")))
            .IsEqualTo("✓ Agent tasks\n  No agent tasks.");
    }

    [Test]
    public async Task Leaf_displays_definition_without_absent_optional_fields()
    {
        var text = string.Join('\n', Render("{\"name\":\"task\"}", Leaf));

        _ = await Assert.That(text).IsEqualTo("✓ Agent task · task\n  task · succeeded\n  Description: Do work\n  Acceptance criteria: Work done\n  Instruction: Run work");
        _ = await Assert.That(text).DoesNotContain("Dependencies:");
        _ = await Assert.That(text).DoesNotContain("Model:");
        _ = await Assert.That(text).DoesNotContain("Result:");
        _ = await Assert.That(text).DoesNotContain("Failure:");
    }

    [Test]
    public async Task Optional_details_and_outcomes_precede_instruction()
    {
        var result = Leaf.Replace("\"dependencies\":[]", "\"dependencies\":[\"prepare\",\"review\"]", StringComparison.Ordinal)
            .Replace("\"state\":\"succeeded\"", "\"state\":\"failed\",\"model\":\"medium_llm\",\"result\":\"Partial work\",\"failure\":\"Missing input\"", StringComparison.Ordinal);
        var lines = Render("{\"name\":\"task\"}", result);

        _ = await Assert.That(string.Join('\n', lines)).Contains("Dependencies: prepare, review\n  Model: medium_llm");
        _ = await Assert.That(string.Join('\n', lines)).Contains("Result: Partial work\n  Failure: Missing input\n  Instruction: Run work");
        _ = await Assert.That(lines[0]).IsEqualTo("✓ Agent task · task");
        _ = await Assert.That(lines[1]).IsEqualTo("  task · failed");
    }

    [Test]
    public async Task Nested_definitions_do_not_invent_states_and_preserve_repeated_names()
    {
        const string result = "{\"name\":\"task\",\"description\":\"Root\",\"dependencies\":[],\"acceptance_criteria\":\"Done\",\"state\":\"running\",\"payload\":[{\"name\":\"repeat\",\"description\":\"Child\",\"dependencies\":[],\"acceptance_criteria\":\"Done\",\"payload\":[{\"name\":\"repeat\",\"description\":\"Grandchild\",\"dependencies\":[],\"acceptance_criteria\":\"Done\",\"payload\":\"Run\"}]}]}";
        var text = string.Join('\n', Render("{\"name\":\"task\"}", result));

        _ = await Assert.That(text).Contains("Declared child definitions:");
        _ = await Assert.That(text).Contains("    repeat · Child");
        _ = await Assert.That(text).Contains("        repeat · Grandchild");
        _ = await Assert.That(text).DoesNotContain("pending");
        _ = await Assert.That(text).DoesNotContain("unknown");
        _ = await Assert.That(text).DoesNotContain("○");
        _ = await Assert.That(text).Contains("lines truncated.");

        var invalid = result.Replace("\"payload\":\"Run\"", "\"payload\":\"Run\",\"state\":\"succeeded\"", StringComparison.Ordinal);
        _ = await Assert.That(Render("{\"name\":\"task\"}", invalid)[0]).Contains("tool call get_agent_tasks");
    }

    [Test]
    public async Task Unicode_multiline_and_control_characters_are_safe_at_narrow_widths()
    {
        const string result = "[{\"name\":\"界😀\",\"state\":\"running\",\"description\":\"First line\\nSecond line\\u001b[2J\"}]";
        var wide = string.Join('\n', Render("{}", result));
        var narrow = RenderAtWidth("{}", result, 18);

        _ = await Assert.That(wide).Contains("界😀");
        _ = await Assert.That(wide).Contains("Second line");
        _ = await Assert.That(wide).DoesNotContain('\u001b');
        foreach (var line in narrow)
        {
            _ = await Assert.That(TerminalText.Width(line)).IsLessThanOrEqualTo(18);
        }
    }

    [Test]
    public async Task Long_instructions_are_explicitly_truncated_after_result()
    {
        var result = Leaf.Replace("\"payload\":\"Run work\"", "\"result\":\"Completed\",\"payload\":\"" + new string('x', 400) + "\"", StringComparison.Ordinal);
        var lines = RenderAtWidth("{\"name\":\"task\"}", result, 40);

        _ = await Assert.That(string.Join('\n', lines)).Contains("Result: Completed");
        _ = await Assert.That(lines[^1]).Contains("lines truncated.");
        _ = await Assert.That(lines.Count).IsLessThanOrEqualTo(10);
    }

    [Test]
    [Arguments("{}", "not json")]
    [Arguments("{}", "{}")]
    [Arguments("{}", "[{}]")]
    [Arguments("{}", "[{\"name\":\"a\",\"state\":\"other\",\"description\":\"b\"}]")]
    [Arguments("{}", "[{\"name\":42,\"state\":\"pending\",\"description\":\"b\"}]")]
    [Arguments("{\"name\":\"task\"}", "[]")]
    [Arguments("not json", "[]")]
    [Arguments("{\"name\":null}", "[]")]
    public async Task Invalid_arguments_or_output_use_generic_fallback(string arguments, string result) =>
        _ = await Assert.That(Render(arguments, result)[0]).Contains("tool call get_agent_tasks");

    [Test]
    [Arguments(ToolTerminalStatus.Succeeded, true, "error: missing task", "", "✗")]
    [Arguments(ToolTerminalStatus.Cancelled, false, "", "", "■")]
    [Arguments(ToolTerminalStatus.Errored, false, "", "transport failed", "✗")]
    [Arguments(ToolTerminalStatus.Succeeded, false, "", "", "✓")]
    public async Task Non_task_outcomes_preserve_generic_status_and_diagnostics(
        ToolTerminalStatus status, bool present, string result, string error, string marker)
    {
        var terminal = new ToolTerminalPresentation(status, present, result, error);
        var item = Registry().PresentTerminal(
            new ToolCallPresentation("get_agent_tasks", "{}"), terminal)
            ?? throw new InvalidOperationException("Terminal output missing.");
        var lines = item.Render(Context);

        _ = await Assert.That(lines[0]).StartsWith(marker);
        _ = await Assert.That(lines[0]).Contains("tool call get_agent_tasks");
        if (present)
        {
            _ = await Assert.That(string.Join('\n', lines)).Contains(result);
        }

        if (error.Length > 0)
        {
            _ = await Assert.That(string.Join('\n', lines)).Contains(error);
        }
    }

    [Test]
    public async Task Unknown_tool_keeps_generic_presentation()
    {
        var item = Registry().PresentTerminal(
            new ToolCallPresentation("unknown", "{}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "[]", string.Empty))
            ?? throw new InvalidOperationException("Terminal output missing.");

        _ = await Assert.That(item.Render(Context)[0]).IsEqualTo("✓ tool call unknown");
    }

    [Test]
    [Arguments("{}", "[{\"name\":\"task\",\"state\":\"running\",\"description\":\"Work\",\"hidden\":true}]")]
    [Arguments("{\"name\":\"task\"}", "{\"name\":\"task\",\"state\":\"succeeded\",\"hidden\":true}")]
    public async Task All_hidden_tasks_produce_no_terminal_task_block(string arguments, string result)
    {
        var item = Registry().PresentTerminal(
            new ToolCallPresentation("get_agent_tasks", arguments),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, result, string.Empty));

        _ = await Assert.That(item).IsNull();
    }

    [Test]
    public async Task Summary_filters_hidden_tasks_but_keeps_legacy_and_explicit_visible_tasks()
    {
        const string result = "[{\"name\":\"hidden\",\"state\":\"running\",\"description\":\"Hidden work\",\"hidden\":true},{\"name\":\"legacy\",\"state\":\"pending\",\"description\":\"Legacy work\"},{\"name\":\"shown\",\"state\":\"failed\",\"description\":\"Shown work\",\"hidden\":false}]";

        _ = await Assert.That(string.Join('\n', Render("{}", result)))
            .IsEqualTo("✓ Agent tasks\n  ○ pending · legacy · Legacy work\n  ✗ failed · shown · Shown work");
    }

    [Test]
    public async Task Named_detail_shows_agent_name_and_omits_hidden_declared_subtrees()
    {
        const string result = "{\"name\":\"task\",\"description\":\"Root\",\"dependencies\":[],\"acceptance_criteria\":\"Done\",\"state\":\"running\",\"agent_name\":\"communicate-with-me\",\"payload\":[{\"name\":\"hidden child\",\"hidden\":true,\"payload\":[{\"name\":\"hidden grandchild\"}]}]}";
        var text = string.Join('\n', Render("{\"name\":\"task\"}", result));

        _ = await Assert.That(text).Contains("Agent: communicate-with-me");
        _ = await Assert.That(text).DoesNotContain("hidden child");
        _ = await Assert.That(text).DoesNotContain("hidden grandchild");
        _ = await Assert.That(text).DoesNotContain("Declared child definitions:");
    }

    private static ToolPresenterRegistry Registry()
    {
        IToolPresenter generic = new GenericToolPresenter();
        return new ToolPresenterRegistry([new GetAgentTasksToolPresenter(generic)], generic);
    }

    private static IReadOnlyList<string> Render(string arguments, string result) =>
        RenderAtWidth(arguments, result, Context.Columns);

    private static IReadOnlyList<string> RenderAtWidth(string arguments, string result, int columns)
    {
        var item = Registry().PresentTerminal(
            new ToolCallPresentation("get_agent_tasks", arguments),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, result, string.Empty))
            ?? throw new InvalidOperationException("Terminal output missing.");
        return item.Render(new ScrollbackRenderContext(columns, new TerminalPalette(false)));
    }
}
