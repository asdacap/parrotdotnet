using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class TerminalTextLayoutTests
{
    [Test]
    [Arguments("hello world", 7, "hello|world")]
    [Arguments("alpha beta gamma", 11, "alpha beta|gamma")]
    [Arguments("  lead", 10, "  lead")]
    [Arguments("one\ntwo", 80, "one|two")]
    [Arguments("ab\ncd", 3, "ab|cd")]
    [Arguments("ab cd", 5, "ab cd")]
    [Arguments("ab cde", 5, "ab|cde")]
    public async Task Word_layout_wraps_at_word_boundaries_and_keeps_newlines(
        string value, int width, string expected)
    {
        var rows = TerminalText.LayoutWords(value, width);

        _ = await Assert.That(string.Join('|', rows)).IsEqualTo(expected);
        _ = await Assert.That(rows.All(row => TerminalText.Width(row) <= width)).IsTrue();
    }

    [Test]
    [Arguments("日本語 alpha", 7, "日本語|alpha")]
    [Arguments("界界界", 5, "界界|界")]
    [Arguments("a👨‍👩‍👧‍👦b", 3, "a👨‍👩‍👧‍👦|b")]
    public async Task Word_layout_keeps_graphemes_intact(
        string value, int width, string expected)
    {
        var rows = TerminalText.LayoutWords(value, width);

        _ = await Assert.That(string.Join('|', rows)).IsEqualTo(expected);
        _ = await Assert.That(rows.All(row => TerminalText.Width(row) <= width)).IsTrue();
    }

    [Test]
    public async Task Word_layout_hard_breaks_a_token_wider_than_the_row()
    {
        var rows = TerminalText.LayoutWords("abcdefgh", 3);
        var mixed = TerminalText.LayoutWords("ab supercalifrag", 5);

        _ = await Assert.That(string.Join('|', rows)).IsEqualTo("abc|def|gh");
        _ = await Assert.That(string.Join('|', mixed)).IsEqualTo("ab su|perca|lifra|g");
        _ = await Assert.That(rows.All(row => TerminalText.Width(row) <= 3)).IsTrue();
        _ = await Assert.That(mixed.All(row => TerminalText.Width(row) <= 5)).IsTrue();
    }

    [Test]
    public async Task Word_layout_hangs_continuation_rows_at_the_indent()
    {
        var rows = TerminalText.LayoutWordsHanging("alpha beta gamma", 11, "  ");
        var overWide = TerminalText.LayoutWordsHanging("ab supercalifrag", 5, "  ");

        _ = await Assert.That(string.Join('|', rows)).IsEqualTo("alpha beta|  gamma");
        _ = await Assert.That(string.Join('|', overWide)).IsEqualTo("ab su|  per|  cal|  ifr|  ag");
        _ = await Assert.That(rows.All(row => TerminalText.Width(row) <= 11)).IsTrue();
        _ = await Assert.That(overWide.All(row => TerminalText.Width(row) <= 5)).IsTrue();
    }

    [Test]
    public async Task Cell_layout_is_retained_for_the_prompt()
    {
        var rows = TerminalText.Layout("abcdef", 3);

        _ = await Assert.That(string.Join('|', rows)).IsEqualTo("abc|def");
    }
}
