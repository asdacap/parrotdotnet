using System.Text.RegularExpressions;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class MarkdownRendererTests
{
    [Test]
    public async Task Formats_block_and_inline_markdown()
    {
        var source = string.Join(
            '\n',
            "# Heading",
            "> quoted",
            "- item",
            "- [x] done",
            "1. ordered",
            "**bold** *italic* ~~gone~~ `code` [site](https://example.com)",
            "---");

        var rendered = MarkdownRenderer.Render("- ", source, 80, false);

        _ = await Assert.That(string.Join('\n', rendered)).IsEqualTo(string.Join(
            '\n',
            "- Heading",
            "  │ quoted",
            "  • item",
            "  • ☑ done",
            "  1. ordered",
            "  bold italic gone code site (https://example.com)",
            "  " + new string('─', 78)));
    }

    [Test]
    [Arguments("\n")]
    [Arguments("  \n")]
    [Arguments("\r\n")]
    public async Task Blank_sources_render_nothing_and_leading_blank_lines_are_dropped(string source)
    {
        var rendered = MarkdownRenderer.Render("● ", source, 80, false);
        var leadingNewline = MarkdownRenderer.Render("● ", "\ndef", 80, false);

        _ = await Assert.That(rendered).IsEmpty();
        _ = await Assert.That(leadingNewline[0]).IsEqualTo("● def");
        _ = await Assert.That(leadingNewline.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Formats_tables_and_fenced_code()
    {
        var source = "| Name | Status |\n| --- | --- |\n| Build | Done |\n\n```csharp\npublic var value = 42;\n```";

        var rendered = MarkdownRenderer.Render(string.Empty, source, 80, false);
        var text = string.Join('\n', rendered);

        _ = await Assert.That(text).Contains("┌───────┬────────┐");
        _ = await Assert.That(text).Contains("│ Name  │ Status │");
        _ = await Assert.That(text).Contains("public var value = 42;");
        _ = await Assert.That(text).DoesNotContain("```");
    }

    [Test]
    public async Task Colorizes_markdown_and_known_code_but_not_unknown_code()
    {
        var known = string.Join('\n', MarkdownRenderer.Render(
            "- ",
            "# Heading\n```csharp\npublic string Value() => \"ok\"; // note\n```",
            80,
            true));
        var unknown = string.Join('\n', MarkdownRenderer.Render(
            string.Empty,
            "```not-a-language\npublic value = 1\n```",
            80,
            true));

        _ = await Assert.That(known).Contains("\u001b[1;36mHeading\u001b[0m");
        _ = await Assert.That(known).Contains("\u001b[1;35mpublic\u001b[0m");
        _ = await Assert.That(known).Contains("\u001b[32m\"ok\"\u001b[0m");
        _ = await Assert.That(unknown).DoesNotContain("\u001b[1;35m");
    }

    [Test]
    public async Task Sanitizes_untrusted_terminal_controls_before_formatting()
    {
        var rendered = string.Join('\n', MarkdownRenderer.Render(
            string.Empty,
            "**safe\u001b[2J**",
            80,
            true));
        var withoutOwnedAnsi = Regex.Replace(
            rendered,
            "\\x1b\\[[0-9;]+m",
            string.Empty,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        _ = await Assert.That(withoutOwnedAnsi).IsEqualTo("safe[2J");
        _ = await Assert.That(rendered).DoesNotContain("\u001b[2J");
    }

    [Test]
    public async Task Reasoning_summaries_format_markdown_with_a_muted_hanging_prefix()
    {
        var plain = new ReasoningSummaryScrollbackValue("# title\n- **item**")
            .Render(new ScrollbackRenderContext(80, new TerminalPalette(false)));
        var wrapped = new ReasoningSummaryScrollbackValue("abcdefg")
            .Render(new ScrollbackRenderContext(8, new TerminalPalette(false)));
        var colored = new ReasoningSummaryScrollbackValue("# title")
            .Render(new ScrollbackRenderContext(80, new TerminalPalette(true)));

        _ = await Assert.That(string.Join('|', plain)).IsEqualTo("✦ title|  • item");
        _ = await Assert.That(string.Join('|', wrapped)).IsEqualTo("✦ abcdef|  g");
        _ = await Assert.That(colored[0]).IsEqualTo("\u001b[38;5;245m✦ title\u001b[0m");
        _ = await Assert.That(colored[0]).DoesNotContain("38;5;195");
    }
}
