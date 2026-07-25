using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class MarkdownLiveRendererTests
{
    [Test]
    public async Task Promotes_complete_source_lines_and_formats_final_markdown_once()
    {
        var renderer = new MarkdownLiveRenderer(static () => 80, false);

        var first = renderer.Append(new LiveTerminalStreamMessage("answer", "- ", "# Head"));
        var promotion = renderer.Append(new LiveTerminalStreamMessage("answer", "- ", "ing\n**bold**"));
        var committed = renderer.Commit();

        _ = await Assert.That(first.Scrollback).IsEmpty();
        _ = await Assert.That(promotion.Scrollback).Contains("- Heading");
        _ = await Assert.That(committed.Scrollback).Contains("  bold");
    }

    [Test]
    public async Task Keeps_fenced_code_live_until_the_closing_fence()
    {
        var renderer = new MarkdownLiveRenderer(static () => 80, false);

        var beforeClose = renderer.Append(
            new LiveTerminalStreamMessage("answer", string.Empty, "```csharp\npublic var value = 1;\n"));
        var afterClose = renderer.Append(new LiveTerminalStreamMessage("answer", string.Empty, "```\n"));

        _ = await Assert.That(beforeClose.Preview).Contains("public var value = 1;");
        _ = await Assert.That(beforeClose.Scrollback).IsEmpty();
        _ = await Assert.That(afterClose.Scrollback).Contains("public var value = 1;");
        _ = await Assert.That(string.Join('\n', afterClose.Scrollback)).DoesNotContain("```");
    }

    [Test]
    public async Task Keeps_table_live_until_its_boundary()
    {
        var renderer = new MarkdownLiveRenderer(static () => 80, false);

        var beforeBoundary = renderer.Append(
            new LiveTerminalStreamMessage("answer", string.Empty, "A | B\n--- | ---\nx | y\n"));
        var boundary = renderer.Append(new LiveTerminalStreamMessage("answer", string.Empty, "after\n"));

        _ = await Assert.That(string.Join('\n', beforeBoundary.Preview)).Contains("┌");
        _ = await Assert.That(beforeBoundary.Scrollback).IsEmpty();
        _ = await Assert.That(string.Join('\n', boundary.Scrollback)).Contains("┌───┬───┐");
    }
}
