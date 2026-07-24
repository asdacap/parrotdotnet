using System.Text;
using System.Text.RegularExpressions;

namespace Parrot.Cli.Tests;

internal sealed class LiveTerminalRendererTests
{
    private const string HideCursor = "\u001b[?25l";
    private const string ShowCursor = "\u001b[?25h";
    private const string EraseLine = "\u001b[2K";

    [Test]
    public async Task Update_and_clear_emit_exact_owned_terminal_bytes(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new LiveTerminalRenderer(output, () => 80);

        await renderer.Update(new LiveTerminalStreamMessage("answer", string.Empty, "one"), cancellationToken);
        await renderer.Clear(cancellationToken);

        var expected = $"{HideCursor}{EraseLine}one{ShowCursor}{HideCursor}\r{EraseLine}\r{ShowCursor}";
        _ = await Assert.That(output.ToString()).IsEqualTo(expected);
        _ = await Assert.That(output.ToString()).DoesNotContain("\u001b[?1049");
    }

    [Test]
    public async Task Only_owned_ansi_survives_and_untrusted_controls_are_sanitized(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new LiveTerminalRenderer(output, () => 80);
        var text = "safe\u001b[2J\nC0\0\a\r C1\u0085\tend\u001b]0;title";

        await renderer.Update(new LiveTerminalStreamMessage("answer", "- ", text), cancellationToken);
        await renderer.Commit(new LiveTerminalStreamMessage("answer", "- ", text), cancellationToken);

        var rendered = output.ToString();
        var withoutOwnedAnsi = Regex.Replace(
            rendered,
            "\\x1b(?:\\[\\?25[lh]|\\[2K|\\[[0-9]+A)",
            string.Empty,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        _ = await Assert.That(withoutOwnedAnsi).DoesNotContain("\u001b");
        _ = await Assert.That(rendered).DoesNotContain("\u001b[2J");
        _ = await Assert.That(rendered).DoesNotContain("\u001b]");
        _ = await Assert.That(rendered).Contains("- safe[2J\n");
        _ = await Assert.That(rendered).Contains("  C0 C1    end]0;title\n");
    }

    [Test]
    public async Task Width_is_resampled_and_a_shrunken_physical_region_is_fully_cleared(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var columns = 10;
        var renderer = new LiveTerminalRenderer(output, () => columns);
        await renderer.Update(new LiveTerminalStreamMessage("answer", "- ", "abcdefgh"), cancellationToken);
        var before = output.GetStringBuilder().Length;

        columns = 6;
        await renderer.Update(new LiveTerminalStreamMessage("answer", "- ", "abcdefgh"), cancellationToken);

        var resize = output.ToString()[before..];
        _ = await Assert.That(resize).Contains("\u001b[1A");
        _ = await Assert.That(Count(resize, EraseLine)).IsEqualTo(3);
        _ = await Assert.That(resize).Contains("- abcd\n");
        _ = await Assert.That(resize).EndsWith($"{EraseLine}  efgh{ShowCursor}");

        using var fallbackOutput = new StringWriter();
        var fallback = new LiveTerminalRenderer(fallbackOutput, () => 0);
        await fallback.Update(
            new LiveTerminalStreamMessage("fallback", string.Empty, new string('x', 81)), cancellationToken);
        _ = await Assert.That(fallbackOutput.ToString()).Contains(new string('x', 80) + "\n");
    }

    [Test]
    public async Task Cumulative_updates_promote_stable_rows_are_idempotent_and_commit_only_the_suffix(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new LiveTerminalRenderer(output, () => 10);
        await renderer.Update(new LiveTerminalStreamMessage("answer", "- ", "abcdefgh"), cancellationToken);
        await renderer.Update(
            new LiveTerminalStreamMessage("answer", "- ", "abcdefghijklmnop"), cancellationToken);
        var afterPromotion = output.GetStringBuilder().Length;

        await renderer.Update(
            new LiveTerminalStreamMessage("answer", "- ", "abcdefghijklmnop"), cancellationToken);
        var repeated = output.ToString()[afterPromotion..];
        var beforeCommit = output.GetStringBuilder().Length;
        await renderer.Commit(
            new LiveTerminalStreamMessage("answer", "- ", "abcdefghijklmnop!"), cancellationToken);
        var committed = output.ToString()[beforeCommit..];

        _ = await Assert.That(repeated).DoesNotContain("abcdefgh\n");
        _ = await Assert.That(committed).DoesNotContain("abcdefgh");
        _ = await Assert.That(committed).Contains("  ijklmnop\n  !\n");
        _ = await Assert.That(Count(output.ToString(), "- abcdefgh\n")).IsEqualTo(1);
    }

    [Test]
    [Arguments("界a", 4, "- 界\n", "  a")]
    [Arguments("e\u0301x", 3, "- e\u0301\n", "  x")]
    [Arguments("👩‍💻x", 4, "- 👩‍💻\n", "  x")]
    [Arguments("🇺🇸x", 4, "- 🇺🇸\n", "  x")]
    [Arguments("♥️x", 4, "- ♥️\n", "  x")]
    [Arguments("a\tb", 5, "- a  \n", "    b")]
    public async Task Unicode_width_combining_sequences_and_tabs_follow_physical_cells(
        string text,
        int columns,
        string promoted,
        string live,
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new LiveTerminalRenderer(output, () => columns);

        await renderer.Update(new LiveTerminalStreamMessage("answer", "- ", text), cancellationToken);

        var rendered = output.ToString();
        _ = await Assert.That(rendered).Contains(promoted);
        _ = await Assert.That(rendered).EndsWith($"{EraseLine}{live}{ShowCursor}");
        _ = await Assert.That(rendered).DoesNotContain("\t");
    }

    [Test]
    public async Task Failed_writes_and_rejected_cumulative_changes_leave_stream_state_unchanged(
        CancellationToken cancellationToken)
    {
        using var output = new FailingTextWriter { Fail = true };
        var renderer = new LiveTerminalRenderer(output, () => 8);
        var initial = new LiveTerminalStreamMessage("answer", "- ", "abc");

        _ = await Assert.That(async () => await renderer.Update(initial, cancellationToken)).Throws<IOException>();
        _ = await Assert.That(output.Text).IsEmpty();

        output.Fail = false;
        await renderer.Update(initial, cancellationToken);
        var beforeFailure = output.Text.Length;
        output.Fail = true;
        _ = await Assert.That(
            async () => await renderer.Update(
                new LiveTerminalStreamMessage("answer", "- ", "abcdefghij"), cancellationToken))
            .Throws<IOException>();
        _ = await Assert.That(output.Text.Length).IsEqualTo(beforeFailure);

        output.Fail = false;
        await renderer.Update(
            new LiveTerminalStreamMessage("answer", "- ", "abcdefghij"), cancellationToken);
        var beforeRejectedChange = output.Text.Length;
        _ = await Assert.That(
            async () => await renderer.Update(
                new LiveTerminalStreamMessage("answer", "- ", "changed"), cancellationToken))
            .Throws<InvalidOperationException>();
        _ = await Assert.That(output.Text.Length).IsEqualTo(beforeRejectedChange);
        _ = await Assert.That(Count(output.Text, "- abcdef\n")).IsEqualTo(1);
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

    private sealed class FailingTextWriter : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        internal bool Fail { get; set; }

        internal string Text => _text.ToString();

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new IOException("scripted write failure");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = _text.Append(buffer.Span);
            return Task.CompletedTask;
        }
    }
}
