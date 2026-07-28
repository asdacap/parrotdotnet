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
        _ = await Assert.That(scrollback[^1]).IsEqualTo("  … display truncated");
        _ = await Assert.That(string.Join('|', scrollback)).DoesNotContain('\u001b');
        _ = await Assert.That(large[^1]).IsEqualTo("  … display truncated");
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
        var patch = new ApplyPatchToolPresenter().PresentTerminal(
            new ToolCallPresentation("main", "apply_patch", "{\"patchText\":\"file.cs\\n-old\\n+new\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Applied patch", string.Empty));
        var spawn = new AgentSpawnToolPresenter().PresentTerminal(
            new ToolCallPresentation("main", "agent_spawn", "{\"prompt\":\"ship it\",\"name\":\"worker\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "{}", string.Empty));
        var read = new ReadToolPresenter().PresentTerminal(
            new ToolCallPresentation("main", "read", "{\"path\":\"src/App.cs\",\"offset\":12}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "12: class App", string.Empty));
        var todos = new TodoReadToolPresenter().PresentTerminal(
            new ToolCallPresentation("main", "todoread", "{}"),
            new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                true,
                "[{\"content\":\"ship it\",\"status\":\"in_progress\"}]",
                string.Empty));
        var wait = new WaitAgentToolPresenter();

        var patchReport = ((IToolPresentationValue)(patch
            ?? throw new InvalidOperationException("Patch report missing."))).Report;
        var spawnReport = ((IToolPresentationValue)(spawn
            ?? throw new InvalidOperationException("Spawn report missing."))).Report;
        var readReport = ((IToolPresentationValue)read).Report;
        var todoReport = ((IToolPresentationValue)todos).Report;

        _ = await Assert.That(patchReport.Block.Kind).IsEqualTo(ToolBlockKind.Diff);
        _ = await Assert.That(spawnReport.Block.Kind).IsEqualTo(ToolBlockKind.CompletedInput);
        _ = await Assert.That(readReport.Block.Kind).IsEqualTo(ToolBlockKind.Code);
        _ = await Assert.That(readReport.Block.Language).IsEqualTo("csharp");
        _ = await Assert.That(readReport.Block.Path).IsEqualTo("src/App.cs");
        _ = await Assert.That(readReport.Block.Line).IsEqualTo(12);
        _ = await Assert.That(todoReport.Block.Kind).IsEqualTo(ToolBlockKind.Todos);
        _ = await Assert.That(spawnReport.Metadata.SuccessIcon).IsEqualTo("♟");
        _ = await Assert.That(spawnReport.Metadata.TerminalOnly).IsTrue();
        _ = await Assert.That(wait.Metadata.LiveOnly).IsTrue();
        _ = await Assert.That(wait.Metadata.Modeline).IsTrue();
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
