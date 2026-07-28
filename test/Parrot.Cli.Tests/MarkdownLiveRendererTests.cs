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

        var context = new ScrollbackRenderContext(80, new TerminalPalette(false));
        _ = await Assert.That(first.Scrollback).IsNull();
        _ = await Assert.That(promotion.Scrollback?.Render(context)).Contains("- Heading");
        _ = await Assert.That(committed.Scrollback?.Render(context)).Contains("  bold");
    }

    [Test]
    public async Task Keeps_fenced_code_live_until_the_closing_fence()
    {
        var renderer = new MarkdownLiveRenderer(static () => 80, false);

        var beforeClose = renderer.Append(
            new LiveTerminalStreamMessage("answer", string.Empty, "```csharp\npublic var value = 1;\n"));
        var afterClose = renderer.Append(new LiveTerminalStreamMessage("answer", string.Empty, "```\n"));

        _ = await Assert.That(beforeClose.Preview).Contains("public var value = 1;");
        var context = new ScrollbackRenderContext(80, new TerminalPalette(false));
        _ = await Assert.That(beforeClose.Scrollback).IsNull();
        _ = await Assert.That(afterClose.Scrollback?.Render(context)).Contains("public var value = 1;");
        _ = await Assert.That(string.Join('\n', afterClose.Scrollback?.Render(context) ?? [])).DoesNotContain("```");
    }

    [Test]
    public async Task Keeps_only_the_last_three_visual_rows_live_until_commit()
    {
        var renderer = new MarkdownLiveRenderer(static () => 8, false);

        var update = renderer.Append(
            new LiveTerminalStreamMessage("answer", string.Empty, "```text\none\ntwo\nthree\nfour\nfive"));
        var committed = renderer.Commit();

        _ = await Assert.That(string.Join('|', update.Preview)).IsEqualTo("three|four|five");
        var context = new ScrollbackRenderContext(8, new TerminalPalette(false));
        _ = await Assert.That(string.Join('\n', committed.Scrollback?.Render(context) ?? []))
            .Contains("one\ntwo\nthree\nfour\nfive");
    }

    [Test]
    public async Task Keeps_table_live_until_its_boundary()
    {
        var renderer = new MarkdownLiveRenderer(static () => 80, false);

        var beforeBoundary = renderer.Append(
            new LiveTerminalStreamMessage("answer", string.Empty, "A | B\n--- | ---\nx | y\n"));
        var boundary = renderer.Append(new LiveTerminalStreamMessage("answer", string.Empty, "after\n"));

        _ = await Assert.That(string.Join('\n', beforeBoundary.Preview)).Contains("└");
        var context = new ScrollbackRenderContext(80, new TerminalPalette(false));
        _ = await Assert.That(beforeBoundary.Scrollback).IsNull();
        _ = await Assert.That(string.Join('\n', boundary.Scrollback?.Render(context) ?? [])).Contains("┌───┬───┐");
    }
}
