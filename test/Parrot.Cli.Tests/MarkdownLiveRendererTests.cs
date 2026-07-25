using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class MarkdownLiveRendererTests
{
    [Test]
    public async Task Promotes_complete_source_lines_and_formats_final_markdown_once(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new MarkdownLiveRenderer(output, static () => 80, false);

        await renderer.Append(new LiveTerminalStreamMessage("answer", "- ", "# Head"), cancellationToken);
        var beforePromotion = output.GetStringBuilder().Length;
        await renderer.Append(
            new LiveTerminalStreamMessage("answer", "- ", "ing\n**bold**"),
            cancellationToken);
        var promotion = output.ToString()[beforePromotion..];
        await renderer.Commit(cancellationToken);

        _ = await Assert.That(promotion).Contains("- Heading\n");
        _ = await Assert.That(output.ToString()).Contains("  bold\n");
        _ = await Assert.That(Count(output.ToString(), "- Heading\n")).IsEqualTo(1);
    }

    [Test]
    public async Task Keeps_fenced_code_live_until_the_closing_fence(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new MarkdownLiveRenderer(output, static () => 80, false);

        await renderer.Append(
            new LiveTerminalStreamMessage("answer", string.Empty, "```csharp\npublic var value = 1;\n"),
            cancellationToken);
        var beforeClose = output.ToString();
        await renderer.Append(
            new LiveTerminalStreamMessage("answer", string.Empty, "```\n"),
            cancellationToken);
        var afterClose = output.ToString()[beforeClose.Length..];

        _ = await Assert.That(beforeClose).Contains("public var value = 1;");
        _ = await Assert.That(beforeClose).DoesNotContain("public var value = 1;\n");
        _ = await Assert.That(afterClose).Contains("public var value = 1;\n");
        _ = await Assert.That(afterClose).DoesNotContain("```");
    }

    [Test]
    public async Task Keeps_table_live_until_its_boundary(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new MarkdownLiveRenderer(output, static () => 80, false);

        await renderer.Append(
            new LiveTerminalStreamMessage("answer", string.Empty, "A | B\n--- | ---\nx | y\n"),
            cancellationToken);
        var beforeBoundary = output.ToString();
        await renderer.Append(
            new LiveTerminalStreamMessage("answer", string.Empty, "after\n"),
            cancellationToken);
        var boundary = output.ToString()[beforeBoundary.Length..];

        _ = await Assert.That(beforeBoundary).Contains("┌");
        _ = await Assert.That(beforeBoundary).DoesNotContain("┌───┬───┐\n");
        _ = await Assert.That(boundary).Contains("┌───┬───┐\n");
    }

    private static int Count(string value, string part)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(part, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += part.Length;
        }

        return count;
    }
}
