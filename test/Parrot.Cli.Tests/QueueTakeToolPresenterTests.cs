using System.Text;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class QueueTakeToolPresenterTests
{
    private static readonly LiveBufferRenderContext LiveContext = new(512, new TerminalPalette(false));
    private static readonly ScrollbackRenderContext ScrollbackContext = new(512, new TerminalPalette(false));

    [Test]
    public async Task Queue_take_renders_remaining_then_taken_items_with_queue_metadata()
    {
        var presenter = new QueueTakeToolPresenter();
        var call = new ToolCallPresentation("worker", "queue_take", "{\"name\":\"work\",\"count\":5}");
        var result = "{\"path\":\"/ignored\",\"name\":\"work\",\"description\":\"release tasks\",\"size\":2,\"closed\":true,\"monitored\":true,\"items\":[\"first\",\"second\"]}";

        var terminal = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, result, string.Empty))
            .Render(ScrollbackContext);
        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines.Select(static line => line.Text).ToArray();

        _ = await Assert.That(string.Join('|', terminal)).IsEqualTo(
            "✓ worker: Take from queue work · up to 5 items · release tasks · closed|  2 remaining|  first|  second");
        _ = await Assert.That(string.Join('|', live)).IsEqualTo("⠋ worker: Take from queue work · up to 5 items");
    }

    [Test]
    public async Task Queue_take_omits_empty_description_and_open_state()
    {
        var presenter = new QueueTakeToolPresenter();
        var call = new ToolCallPresentation("main", "queue_take", "{\"name\":\"work\"}");
        var rendered = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                true,
                "{\"name\":\"work\",\"size\":0,\"closed\":false,\"items\":[]}",
                string.Empty))
            .Render(ScrollbackContext);
        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines.Select(static line => line.Text).ToArray();

        _ = await Assert.That(string.Join('|', rendered)).IsEqualTo(
            "✓ main: Take from queue work · up to 1 item|  0 remaining");
        _ = await Assert.That(string.Join('|', live)).IsEqualTo("⠋ main: Take from queue work · up to 1 item");
    }

    [Test]
    public async Task Queue_take_error_uses_the_input_name_and_error_block()
    {
        var presenter = new QueueTakeToolPresenter();
        var rendered = presenter.PresentTerminal(
            new ToolCallPresentation("main", "queue_take", "{\"name\":\"work\",\"count\":3}"),
            new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                true,
                "error: queue: 'work' is unavailable",
                string.Empty))
            .Render(ScrollbackContext);

        _ = await Assert.That(rendered[0]).IsEqualTo("✗ main: Take from queue work · up to 3 items");
        _ = await Assert.That(rendered[1]).IsEqualTo("  error: queue: 'work' is unavailable");
    }

    [Test]
    public async Task Queue_take_uses_30_rendered_lines_and_standard_queue_sanitization_and_byte_bound()
    {
        var presenter = new QueueTakeToolPresenter();
        var items = Enumerable.Range(1, 40)
            .Select(static value => $"item {value}\u001b[2J")
            .ToArray();
        var result = "{\"name\":\"work\",\"size\":40,\"closed\":false,\"items\":["
            + string.Join(',', items.Select(static item => $"\"{item.Replace("\u001b", "\\u001b", StringComparison.Ordinal)}\""))
            + "]}";
        var rendered = presenter.PresentTerminal(
            new ToolCallPresentation("main", "queue_take", "{\"name\":\"work\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, result, string.Empty))
            .Render(ScrollbackContext);
        var large = presenter.PresentTerminal(
            new ToolCallPresentation("main", "queue_take", "{\"name\":\"work\"}"),
            new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                true,
                $"{{\"name\":\"work\",\"size\":1,\"closed\":false,\"items\":[\"{new string('界', 8_000)}\"]}}",
                string.Empty))
            .Render(new ScrollbackRenderContext(32_768, new TerminalPalette(false)));

        _ = await Assert.That(rendered).Count().IsEqualTo(30);
        _ = await Assert.That(rendered[1]).Contains("40 remaining");
        _ = await Assert.That(rendered[^1]).Contains("lines truncated.");
        _ = await Assert.That(string.Join('|', rendered)).DoesNotContain('\u001b');
        _ = await Assert.That(Encoding.UTF8.GetByteCount(string.Concat(large))).IsLessThanOrEqualTo((16 * 1024) + 64);
    }

    [Test]
    [Arguments("{\"name\":\"work\",\"count\":0}")]
    [Arguments("{\"name\":\"work\",\"count\":-1}")]
    [Arguments("{\"name\":\"work\",\"count\":1.5}")]
    [Arguments("{\"name\":\"work\",\"count\":\"2\"}")]
    [Arguments("{\"name\":\"work\",\"count\":2147483648}")]
    public async Task Queue_take_invalid_count_falls_back_safely_through_the_registry(string arguments)
    {
        var registry = new ToolPresenterRegistry([new QueueTakeToolPresenter()], new GenericToolPresenter());
        var rendered = (registry.PresentTerminal(
            new ToolCallPresentation("main", "queue_take", arguments),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, false, string.Empty, string.Empty))
            ?? throw new InvalidOperationException("Fallback presentation missing."))
            .Render(ScrollbackContext);

        _ = await Assert.That(rendered[0]).IsEqualTo("✓ main: tool call queue_take");
    }

    [Test]
    public async Task Queue_take_malformed_input_or_result_falls_back_safely_through_the_registry()
    {
        var registry = new ToolPresenterRegistry([new QueueTakeToolPresenter()], new GenericToolPresenter());
        var malformedInput = (registry.PresentTerminal(
            new ToolCallPresentation("main", "queue_take", "{\"name\":false}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, false, string.Empty, string.Empty))
            ?? throw new InvalidOperationException("Fallback presentation missing."))
            .Render(ScrollbackContext);
        var malformedResult = (registry.PresentTerminal(
            new ToolCallPresentation("main", "queue_take", "{\"name\":\"work\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "{\"name\":\"work\"}", string.Empty))
            ?? throw new InvalidOperationException("Fallback presentation missing."))
            .Render(ScrollbackContext);

        _ = await Assert.That(malformedInput[0]).IsEqualTo("✓ main: tool call queue_take");
        _ = await Assert.That(malformedResult[0]).IsEqualTo("✓ main: tool call queue_take");
        _ = await Assert.That(string.Join('\n', malformedResult)).Contains("name: \"work\"");
    }
}
