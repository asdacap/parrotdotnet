using System.Text;

namespace Parrot.Cli.Tests;

internal sealed class TerminalInputTests
{
    [Test]
    public async Task Enhanced_terminal_enables_and_disables_bracketed_paste(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();

        await EnhancedCli.SetBracketedPaste(output, true, cancellationToken);
        await EnhancedCli.SetBracketedPaste(output, false, cancellationToken);

        _ = await Assert.That(output.ToString()).IsEqualTo("\u001b[?2004h\u001b[?2004l");
    }

    [Test]
    public async Task Decoder_preserves_incremental_sequences_and_sanitizes_paste()
    {
        var decoder = new TerminalKeyDecoder();

        var first = decoder.Feed([0x1b]);
        var arrow = decoder.Feed(Encoding.ASCII.GetBytes("[D"));
        var runeStart = decoder.Feed([0xf0, 0x9f]);
        var runeEnd = decoder.Feed([0x99, 0x82]);
        var pasteStart = decoder.Feed(Encoding.ASCII.GetBytes("\u001b[200~first\r"));
        var pasteEnd = decoder.Feed(Encoding.ASCII.GetBytes("\nsecond\u001b[2J\u001b[201~"));
        var pendingEscape = decoder.Feed([0x1b]);
        var escape = decoder.Flush();

        _ = await Assert.That(first).IsEmpty();
        _ = await Assert.That(arrow.Count).IsEqualTo(1);
        _ = await Assert.That(arrow[0]).IsEqualTo(new TerminalKey(TerminalKeyKind.Left));
        _ = await Assert.That(runeStart).IsEmpty();
        _ = await Assert.That(runeEnd.Count).IsEqualTo(1);
        _ = await Assert.That(runeEnd[0]).IsEqualTo(new TerminalKey(TerminalKeyKind.Character, "🙂"));
        _ = await Assert.That(pasteStart).IsEmpty();
        _ = await Assert.That(pasteEnd.Count).IsEqualTo(1);
        _ = await Assert.That(pasteEnd[0]).IsEqualTo(
            new TerminalKey(TerminalKeyKind.Paste, "first\nsecond[2J"));
        _ = await Assert.That(pendingEscape).IsEmpty();
        _ = await Assert.That(escape.Count).IsEqualTo(1);
        _ = await Assert.That(escape[0]).IsEqualTo(new TerminalKey(TerminalKeyKind.Escape));
    }

    [Test]
    [Arguments(0x0a)]
    [Arguments(0x0d)]
    public async Task Decoder_accepts_both_terminal_enter_encodings(int value)
    {
        var decoded = new TerminalKeyDecoder().Feed([(byte)value]);

        _ = await Assert.That(decoded.Count).IsEqualTo(1);
        _ = await Assert.That(decoded[0]).IsEqualTo(new TerminalKey(TerminalKeyKind.Submit));
    }

    [Test]
    public async Task Bracketed_multiline_paste_remains_in_the_editor_until_enter()
    {
        var decoder = new TerminalKeyDecoder();
        var editor = new IncrementalEditor("> ", 64 * 1024);
        var decoded = decoder.Feed(Encoding.UTF8.GetBytes("\u001b[200~first\r\nsecond\u001b[201~\r"));

        var beforeSubmit = editor.Apply(decoded[0]);
        var submitted = editor.Apply(decoded[1]);

        _ = await Assert.That(decoded.Count).IsEqualTo(2);
        _ = await Assert.That(beforeSubmit).IsNull();
        _ = await Assert.That(submitted).IsEqualTo("first\nsecond");
    }

    [Test]
    public async Task Decoder_bounds_bracketed_paste_to_sixty_four_kibibytes()
    {
        var decoder = new TerminalKeyDecoder();
        var content = new string('x', (64 * 1024) + 1024);
        var encoded = Encoding.ASCII.GetBytes($"\u001b[200~{content}\u001b[201~z");

        var decoded = decoder.Feed(encoded);

        _ = await Assert.That(decoded.Count).IsEqualTo(2);
        _ = await Assert.That(decoded[0].Kind).IsEqualTo(TerminalKeyKind.Paste);
        _ = await Assert.That(decoded[0].Text.Length).IsEqualTo(64 * 1024);
        _ = await Assert.That(decoded[1]).IsEqualTo(new TerminalKey(TerminalKeyKind.Character, "z"));
    }

    [Test]
    public async Task Editor_applies_rune_aware_multiline_edits_and_limits_input()
    {
        var editor = new IncrementalEditor("> ", 5);

        _ = editor.Apply(new TerminalKey(TerminalKeyKind.Character, "a🙂c"));
        _ = editor.Apply(new TerminalKey(TerminalKeyKind.Left));
        _ = editor.Apply(new TerminalKey(TerminalKeyKind.Newline));
        _ = editor.Apply(new TerminalKey(TerminalKeyKind.Character, "xy"));
        _ = editor.Apply(new TerminalKey(TerminalKeyKind.Home));
        _ = editor.Apply(new TerminalKey(TerminalKeyKind.KillLine));
        var submitted = editor.Apply(new TerminalKey(TerminalKeyKind.Submit));

        _ = await Assert.That(submitted).IsEqualTo("a🙂");
        _ = await Assert.That(editor.Prompt).IsEqualTo(new PromptValue("> ", string.Empty, 0));
    }

    [Test]
    [Arguments("chat", "working", "provider/model", 34, "chat · working      provider/model")]
    [Arguments("chat", "", "provider/model", 8, "provider")]
    [Arguments("chat", "idle", "", 4, "chat")]
    public async Task Modeline_aligns_or_clips_terminal_safe_values(
        string mode, string activity, string model, int width, string expected)
    {
        var rendered = new ModelineValue(mode, activity, model).Render(width);

        _ = await Assert.That(rendered).IsEqualTo(expected);
        _ = await Assert.That(TerminalText.Width(rendered)).IsLessThanOrEqualTo(width);
    }
}
