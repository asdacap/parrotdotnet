using System.Text;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class ToolPresenterRegistryTests
{
    private static readonly LiveBufferRenderContext LiveContext = new(32_768, new TerminalPalette(false));
    private static readonly ScrollbackRenderContext ScrollbackContext = new(32_768, new TerminalPalette(false));

    [Test]
    public async Task Registry_selects_by_ordinal_name_and_falls_back_for_unknown_or_invalid_presenters()
    {
        var selected = new FixedToolPresenter("read", "selected", false);
        var invalid = new FixedToolPresenter("broken", "unused", true);
        var registry = new ToolPresenterRegistry([selected, invalid, new LiveOnlyToolPresenter()], new GenericToolPresenter());
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            false,
            string.Empty,
            string.Empty);

        var known = registry.PresentLive(new ToolCallPresentation("main", "read", "{}"), 0)
            .Render(LiveContext).Lines[0].Text;
        var differentlyCased = registry.PresentLive(new ToolCallPresentation("main", "Read", "{}"), 0)
            .Render(LiveContext).Lines[0].Text;
        var invalidLive = registry.PresentLive(new ToolCallPresentation("main", "broken", "{}"), 0)
            .Render(LiveContext).Lines[0].Text;
        var invalidTerminalItem = registry.PresentTerminal(
            new ToolCallPresentation("main", "broken", "{}"), terminal)
            ?? throw new InvalidOperationException("The generic presenter must produce terminal output.");
        var invalidTerminal = invalidTerminalItem.Render(ScrollbackContext)[0];
        var omitted = registry.PresentTerminal(
            new ToolCallPresentation("main", "live_only", "{}"), terminal);

        _ = await Assert.That(known).IsEqualTo("selected");
        _ = await Assert.That(differentlyCased).Contains("main: Read");
        _ = await Assert.That(invalidLive).Contains("main: broken");
        _ = await Assert.That(invalidTerminal).Contains("main: tool call broken");
        _ = await Assert.That(omitted).IsNull();
    }

    [Test]
    public async Task Generic_presenter_formats_json_inputs_and_results_as_yaml()
    {
        var presenter = new GenericToolPresenter();
        var call = new ToolCallPresentation(
            "main",
            "unknown",
            "{\"path\":\"src/App.cs\",\"options\":{\"limit\":2},\"enabled\":true,\"ambiguous\":\"true\"}");
        var terminal = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                true,
                "[{\"name\":\"first\"},{\"name\":\"second\"}]",
                string.Empty));
        var invalid = presenter.PresentTerminal(
            new ToolCallPresentation("main", "unknown", "not json"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "plain result", string.Empty));
        var duplicate = presenter.PresentLive(
            new ToolCallPresentation("main", "unknown", "{\"value\":1,\"value\":2}"),
            0);
        var live = (IToolPresentationValue)presenter.PresentLive(call, 0);

        var terminalText = ((IToolPresentationValue)terminal).Report.Block.Text;
        var invalidText = ((IToolPresentationValue)invalid).Report.Block.Text;

        _ = await Assert.That(live.Report.Block.Text).Contains("path: \"src/App.cs\"");
        _ = await Assert.That(live.Report.Block.Text).Contains("limit: 2");
        _ = await Assert.That(terminalText).Contains("enabled: true");
        _ = await Assert.That(terminalText).Contains("ambiguous: \"true\"");
        _ = await Assert.That(terminalText).Contains("---\n- name: \"first\"");
        _ = await Assert.That(terminalText).DoesNotContain("{\"");
        _ = await Assert.That(invalidText).IsEqualTo("not json\n---\nplain result");
        _ = await Assert.That(duplicate.Render(LiveContext).Lines[1].Text).Contains("{\"value\":1,\"value\":2}");
    }

    [Test]
    public async Task Registry_redacts_sensitive_input_and_terminal_details_before_specialized_and_fallback_presenters()
    {
        var registry = new ToolPresenterRegistry([new SensitiveFailingToolPresenter()], new GenericToolPresenter());
        const string inputSecret = "secret 🔒";
        const string resultSecret = "private result";
        var call = new ToolCallPresentation(
            "main",
            "sensitive",
            "{\"name\":\"build\",\"input\":\"secret \\uD83D\\uDD12\"}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, resultSecret, string.Empty);

        var live = registry.PresentLive(call, 0).Render(LiveContext).Lines.Select(line => line.Text);
        var finished = registry.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Fallback terminal presentation missing.");
        var rendered = finished.Render(ScrollbackContext);

        _ = await Assert.That(string.Join('\n', live)).Contains("<redacted: 8 chars>");
        _ = await Assert.That(string.Join('\n', rendered)).Contains("<redacted: 8 chars>");
        _ = await Assert.That(string.Join('\n', live)).DoesNotContain(inputSecret);
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain(inputSecret);
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain(resultSecret);
    }

    [Test]
    public async Task Registry_replaces_malformed_sensitive_input_with_constant_marker()
    {
        var registry = new ToolPresenterRegistry([new SensitiveFailingToolPresenter()], new GenericToolPresenter());
        const string malformed = "{\"input\":\"secret";
        var call = new ToolCallPresentation("main", "sensitive", malformed);

        var live = registry.PresentLive(call, 0).Render(LiveContext).Lines.Select(line => line.Text);

        _ = await Assert.That(string.Join('\n', live)).Contains("<redacted>");
        _ = await Assert.That(string.Join('\n', live)).DoesNotContain("secret");
        _ = await Assert.That(string.Join('\n', live)).DoesNotContain(malformed);
    }

    [Test]
    public async Task Historical_todo_calls_use_the_generic_presenter_for_malformed_input_and_spill_output()
    {
        var generic = new GenericToolPresenter();
        var registry = new ToolPresenterRegistry([], generic);
        const string notice = "Tool output exceeded 64 KiB and was saved to /tmp/output.";
        var live = registry.PresentLive(new ToolCallPresentation("main", "todoread", "not json"), 0)
            .Render(LiveContext).Lines.Select(line => line.Text);
        var presented = registry.PresentTerminal(
            new ToolCallPresentation("main", "todowrite", "not json"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, notice, string.Empty))
            ?? throw new InvalidOperationException("The generic presenter must render terminal output.");
        var rendered = presented.Render(ScrollbackContext);

        var liveText = string.Join('\n', live);
        var renderedText = string.Join('\n', rendered);

        _ = await Assert.That(liveText).Contains("main: todoread");
        _ = await Assert.That(liveText).Contains("not json");
        _ = await Assert.That(renderedText).Contains("main: tool call todowrite");
        _ = await Assert.That(renderedText).Contains("not json");
        _ = await Assert.That(renderedText).Contains(notice);
    }

    [Test]
    public async Task Registry_redacts_sensitive_presentations_before_falling_back()
    {
        var registry = new ToolPresenterRegistry([new SensitiveFailingToolPresenter()], new GenericToolPresenter());
        var call = new ToolCallPresentation(
            "main",
            "sensitive",
            "{\"input\":\"secret😀\",\"name\":\"worker\"}");
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Errored,
            true,
            "secret result",
            "secret error");

        var live = registry.PresentLive(call, 0).Render(LiveContext).Lines.Select(line => line.Text).ToArray();
        var completed = registry.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("The fallback presenter must render terminal output.");
        var scrollback = completed.Render(ScrollbackContext);
        var rendered = string.Join('\n', live.Concat(scrollback));

        _ = await Assert.That(rendered).Contains("<redacted: 7 chars>");
        _ = await Assert.That(rendered).Contains("worker");
        _ = await Assert.That(rendered).DoesNotContain("secret😀");
        _ = await Assert.That(rendered).DoesNotContain("secret result");
        _ = await Assert.That(rendered).DoesNotContain("secret error");
    }

    [Test]
    public async Task Redactor_conceals_non_string_fields_and_invalid_json()
    {
        var metadata = ToolPresentationMetadata.Default with
        {
            RedactedInputFields = ["input"],
        };
        var structured = ToolPresentationRedactor.Redact(
            new ToolCallPresentation("main", "sensitive", "{\"input\":{\"secret\":true},\"safe\":42}"),
            metadata);
        var invalid = ToolPresentationRedactor.Redact(
            new ToolCallPresentation("main", "sensitive", "not-json secret"),
            metadata);

        _ = await Assert.That(structured.ArgumentsJson).Contains("<redacted>");
        _ = await Assert.That(structured.ArgumentsJson).Contains("\"safe\":42");
        _ = await Assert.That(structured.ArgumentsJson).DoesNotContain("secret");
        _ = await Assert.That(invalid.ArgumentsJson).IsEqualTo("<redacted>");
    }

    [Test]
    public async Task Display_values_sanitize_and_bound_labels_lines_and_utf8_details()
    {
        var lines = Enumerable.Range(0, 12).Select(index => $"line-{index}\u001b[2J");
        var scrollback = new ToolScrollbackValue(
            "safe\u001b[31m\nignored",
            lines,
            ToolTerminalStatus.Errored).Render(ScrollbackContext);
        var large = new ToolScrollbackValue(
            "large",
            [new string('界', 8_000)],
            ToolTerminalStatus.Succeeded).Render(ScrollbackContext);
        var live = new ToolLiveValue(
            "live\u001b[31m\nignored",
            ["detail\u001b[2J"],
            0).Render(LiveContext).Lines.Select(line => line.Text).ToArray();
        var boundedLabel = new ToolLiveValue(new string('界', 8_000), [], 0)
            .Render(LiveContext).Lines[0].Text;

        _ = await Assert.That(scrollback.Count).IsEqualTo(10);
        _ = await Assert.That(scrollback[0]).IsEqualTo("✗ safe[31m");
        _ = await Assert.That(scrollback[^1]).IsEqualTo("  .. 2 lines truncated.");
        _ = await Assert.That(string.Join('|', scrollback)).DoesNotContain('\u001b');
        _ = await Assert.That(large[^1]).IsEqualTo("  .. 1 lines truncated.");
        _ = await Assert.That(Encoding.UTF8.GetByteCount(string.Concat(large))).IsLessThanOrEqualTo((16 * 1024) + 32);
        _ = await Assert.That(live[0]).IsEqualTo("⠋ live[31m");
        _ = await Assert.That(live[1]).IsEqualTo("  detail[2J");
        _ = await Assert.That(Encoding.UTF8.GetByteCount(boundedLabel)).IsLessThanOrEqualTo(1_032);
        _ = await Assert.That(boundedLabel).EndsWith("…");
        _ = await Assert.That(new ToolScrollbackValue(
            "wrap",
            [new string('x', 100)],
            ToolTerminalStatus.Succeeded).Render(new ScrollbackRenderContext(4, new TerminalPalette(false))).Count)
            .IsLessThanOrEqualTo(10);
    }

    [Test]
    public async Task Reports_expose_semantic_blocks_and_presenter_metadata()
    {
        var spawn = new AgentSpawnToolPresenter().PresentTerminal(
            new ToolCallPresentation("main", "agent_spawn", "{\"prompt\":\"ship it\",\"name\":\"worker\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "{}", string.Empty));
        var read = new ReadToolPresenter().PresentTerminal(
            new ToolCallPresentation("main", "read", "{\"path\":\"src/App.cs\",\"offset\":12}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "12: class App", string.Empty));

        var spawnReport = ((IToolPresentationValue)(spawn
            ?? throw new InvalidOperationException("Spawn report missing."))).Report;
        var readReport = ((IToolPresentationValue)read).Report;
        var readLines = read.Render(new ScrollbackRenderContext(32_768, new TerminalPalette(true)));

        _ = await Assert.That(spawnReport.Block.Kind).IsEqualTo(ToolBlockKind.CompletedInput);
        _ = await Assert.That(readReport.Block.Kind).IsEqualTo(ToolBlockKind.None);
        _ = await Assert.That(readReport.Block.Text).IsEmpty();
        _ = await Assert.That(readLines).Count().IsEqualTo(1);
        _ = await Assert.That(readLines[0]).Contains("\u001b[38;5;245m✓ main: read src/App.cs\u001b[0m");
        _ = await Assert.That(string.Join('\n', readLines)).DoesNotContain("12: class App");
        _ = await Assert.That(spawnReport.Metadata.SuccessIcon).IsEqualTo("♟");
        _ = await Assert.That(spawnReport.Metadata.TerminalOnly).IsTrue();
    }

    private sealed class SensitiveFailingToolPresenter : IToolPresenter
    {
        public string ToolName => "sensitive";

        public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
        {
            RedactedInputFields = ["input"],
            SuppressTerminalDetails = true,
        };

        public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
            throw new FormatException("Use fallback.");

        public IScrollbackItem? PresentTerminal(
            ToolCallPresentation call,
            ToolTerminalPresentation terminal) => throw new FormatException("Use fallback.");
    }

    private sealed class LiveOnlyToolPresenter : IToolPresenter
    {
        public string ToolName => "live_only";

        public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) => new LiveTextValue("live");

        public IScrollbackItem? PresentTerminal(
            ToolCallPresentation call,
            ToolTerminalPresentation terminal) => null;
    }

    private sealed class FixedToolPresenter(string toolName, string live, bool fail) : IToolPresenter
    {
        public string ToolName => toolName;

        public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) => fail
            ? throw new FormatException("invalid arguments")
            : new LiveTextValue(live);

        public IScrollbackItem? PresentTerminal(
            ToolCallPresentation call,
            ToolTerminalPresentation terminal) => fail
                ? throw new FormatException("invalid arguments")
                : ImmediateScrollbackValue.Muted([live]);
    }
}
