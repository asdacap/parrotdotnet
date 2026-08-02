using System.Text;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class QueuePushToolPresenterTests
{
    private static readonly LiveBufferRenderContext LiveContext = new(512, new TerminalPalette(false));
    private static readonly ScrollbackRenderContext ScrollbackContext = new(512, new TerminalPalette(false));

    [Test]
    public async Task Queue_push_renders_items_after_an_open_or_closed_state_header()
    {
        var presenter = new QueuePushToolPresenter();
        var open = presenter.PresentTerminal(
            new ToolCallPresentation("main", "queue_push", "{\"name\":\"work\",\"items\":[\"first\",\"second\"]}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "{}", string.Empty))
            .Render(ScrollbackContext);
        var closed = presenter.PresentTerminal(
            new ToolCallPresentation("main", "queue_push", "{\"name\":\"work\",\"items\":[\"final\"],\"close\":true}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "{}", string.Empty))
            .Render(ScrollbackContext);
        var live = presenter.PresentLive(
            new ToolCallPresentation("main", "queue_push", "{\"name\":\"work\",\"items\":[\"final\"],\"close\":true}"),
            0).Render(LiveContext).Lines.Select(static line => line.Text).ToArray();

        _ = await Assert.That(string.Join('|', open)).IsEqualTo("✓ main: Push to queue work · open|  first|  second");
        _ = await Assert.That(string.Join('|', closed)).IsEqualTo("✓ main: Push to queue work · closed|  final");
        _ = await Assert.That(string.Join('|', live)).IsEqualTo("⠋ main: Push to queue work · closed|  final");
    }

    [Test]
    public async Task Queue_push_renders_source_file_path_without_loading_contents()
    {
        var presenter = new QueuePushToolPresenter();
        const string arguments = "{\"name\":\"work\",\"source_file\":\"tasks/items.txt\",\"close\":true}";
        var terminal = presenter.PresentTerminal(
            new ToolCallPresentation("main", "queue_push", arguments),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "{}", string.Empty))
            .Render(ScrollbackContext);
        var live = presenter.PresentLive(
            new ToolCallPresentation("main", "queue_push", arguments),
            0).Render(LiveContext).Lines.Select(static line => line.Text).ToArray();

        _ = await Assert.That(string.Join('|', terminal))
            .IsEqualTo("✓ main: Push to queue work · closed|  source_file: tasks/items.txt");
        _ = await Assert.That(string.Join('|', live))
            .IsEqualTo("⠋ main: Push to queue work · closed|  source_file: tasks/items.txt");
    }

    [Test]
    public async Task Queue_push_source_file_failure_renders_the_error_instead_of_success_details()
    {
        var presenter = new QueuePushToolPresenter();
        var rendered = presenter.PresentTerminal(
            new ToolCallPresentation(
                "main",
                "queue_push",
                "{\"name\":\"work\",\"source_file\":\"private.txt\"}"),
            new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                true,
                "error: access denied",
                string.Empty))
            .Render(ScrollbackContext);

        _ = await Assert.That(rendered[0]).IsEqualTo("✗ main: Push to queue work · open");
        _ = await Assert.That(string.Join('|', rendered)).Contains("error: access denied");
        _ = await Assert.That(string.Join('|', rendered)).DoesNotContain("private.txt");
    }

    [Test]
    public async Task Queue_push_closed_queue_failure_keeps_the_closed_state_in_its_header()
    {
        var presenter = new QueuePushToolPresenter();
        var rendered = presenter.PresentTerminal(
            new ToolCallPresentation("main", "queue_push", "{\"name\":\"work\",\"items\":[\"again\"]}"),
            new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                true,
                "error: queue: 'work' is closed",
                string.Empty))
            .Render(ScrollbackContext);

        _ = await Assert.That(rendered[0]).IsEqualTo("✗ main: Push to queue work · closed");
        _ = await Assert.That(rendered[1]).IsEqualTo("  error: queue: 'work' is closed");
    }

    [Test]
    public async Task Queue_push_uses_30_rendered_lines_and_standard_detail_sanitization_and_byte_bound()
    {
        var presenter = new QueuePushToolPresenter();
        var items = Enumerable.Range(1, 40)
            .Select(static value => $"item {value}\u001b[2J")
            .ToArray();
        var arguments = "{\"name\":\"work\",\"items\":["
            + string.Join(',', items.Select(static item => $"\"{item.Replace("\u001b", "\\u001b", StringComparison.Ordinal)}\""))
            + "]}";
        var rendered = presenter.PresentTerminal(
            new ToolCallPresentation("main", "queue_push", arguments),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "{}", string.Empty))
            .Render(ScrollbackContext);
        var large = presenter.PresentTerminal(
            new ToolCallPresentation("main", "queue_push", $"{{\"name\":\"work\",\"items\":[\"{new string('界', 8_000)}\"]}}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "{}", string.Empty))
            .Render(new ScrollbackRenderContext(32_768, new TerminalPalette(false)));

        _ = await Assert.That(rendered).Count().IsEqualTo(30);
        _ = await Assert.That(rendered[0]).Contains("open");
        _ = await Assert.That(rendered[^1]).Contains("lines truncated.");
        _ = await Assert.That(string.Join('|', rendered)).DoesNotContain('\u001b');
        _ = await Assert.That(Encoding.UTF8.GetByteCount(string.Concat(large))).IsLessThanOrEqualTo((16 * 1024) + 64);
    }

    [Test]
    [Arguments("{\"name\":\"work\"}")]
    [Arguments("{\"name\":\"work\",\"items\":[],\"source_file\":\"items.txt\"}")]
    [Arguments("{\"name\":\"work\",\"source_file\":42}")]
    public async Task Queue_push_malformed_arguments_fall_back_safely_through_the_registry(string arguments)
    {
        var registry = new ToolPresenterRegistry([new QueuePushToolPresenter()], new GenericToolPresenter());
        var rendered = (registry.PresentTerminal(
            new ToolCallPresentation("main", "queue_push", arguments),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, false, string.Empty, string.Empty))
            ?? throw new InvalidOperationException("Fallback presentation missing."))
            .Render(ScrollbackContext);

        _ = await Assert.That(rendered[0]).IsEqualTo("✓ main: tool call queue_push");
        _ = await Assert.That(string.Join('\n', rendered)).Contains("name: \"work\"");
    }
}
