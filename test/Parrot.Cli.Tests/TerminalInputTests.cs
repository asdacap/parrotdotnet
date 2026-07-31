using System.Text;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class TerminalInputTests
{
    [Test]
    public async Task Enhanced_terminal_enables_and_disables_input_protocols(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();

        await EnhancedCli.SetBracketedPaste(output, true, cancellationToken);
        await EnhancedCli.SetKeyboardEnhancement(output, true, cancellationToken);
        await EnhancedCli.SetKeyboardEnhancement(output, false, cancellationToken);
        await EnhancedCli.SetBracketedPaste(output, false, cancellationToken);

        _ = await Assert.That(output.ToString()).IsEqualTo("\u001b[?2004h\u001b[>1u\u001b[<u\u001b[?2004l");
    }

    // Application cursor key mode swaps CSI for SS3 on the arrows; tmux and screen set it
    // whatever this process does.
    [Test]
    [Arguments("\u001b[A", TerminalKeyKind.Up)]
    [Arguments("\u001bOA", TerminalKeyKind.Up)]
    [Arguments("\u001b[B", TerminalKeyKind.Down)]
    [Arguments("\u001bOB", TerminalKeyKind.Down)]
    [Arguments("\u001b[C", TerminalKeyKind.Right)]
    [Arguments("\u001bOC", TerminalKeyKind.Right)]
    [Arguments("\u001b[D", TerminalKeyKind.Left)]
    [Arguments("\u001bOD", TerminalKeyKind.Left)]
    public async Task Decoder_maps_normal_and_application_cursor_keys(string sequence, TerminalKeyKind kind)
    {
        var decoded = new TerminalKeyDecoder().Feed(Encoding.ASCII.GetBytes(sequence));

        _ = await Assert.That(decoded).HasSingleItem();
        _ = await Assert.That(decoded[0]).IsEqualTo(new TerminalKey(kind));
    }

    [Test]
    public async Task Decoder_maps_shift_tab_to_mode()
    {
        var decoded = new TerminalKeyDecoder().Feed(Encoding.ASCII.GetBytes("\u001b[Z"));

        _ = await Assert.That(decoded).HasSingleItem();
        _ = await Assert.That(decoded[0]).IsEqualTo(new TerminalKey(TerminalKeyKind.Mode));
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
    public async Task Decoder_reset_discards_partial_escape_and_paste_state()
    {
        var decoder = new TerminalKeyDecoder();

        _ = decoder.Feed([0x1b]);
        decoder.Reset();
        var textAfterEscape = decoder.Feed(Encoding.ASCII.GetBytes("[A"));

        _ = decoder.Feed(Encoding.ASCII.GetBytes("\u001b[200~discarded"));
        decoder.Reset();
        var pasteAfterReset = decoder.Feed(Encoding.ASCII.GetBytes("\u001b[200~fresh\u001b[201~"));

        _ = await Assert.That(textAfterEscape.Count).IsEqualTo(2);
        _ = await Assert.That(textAfterEscape[0]).IsEqualTo(new TerminalKey(TerminalKeyKind.Character, "["));
        _ = await Assert.That(textAfterEscape[1]).IsEqualTo(new TerminalKey(TerminalKeyKind.Character, "A"));
        _ = await Assert.That(pasteAfterReset).HasSingleItem();
        _ = await Assert.That(pasteAfterReset[0]).IsEqualTo(new TerminalKey(TerminalKeyKind.Paste, "fresh"));
    }

    [Test]
    public async Task Live_input_cancellation_discards_buffered_keys(CancellationToken cancellationToken)
    {
        using var terminal = new ScriptedTerminal(80);
        var host = new EnhancedLiveInputHost(terminal, static (_, _) => Task.CompletedTask);
        terminal.Type("ab");
        var first = await host.ReadKey(cancellationToken);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = await Assert.That(async () => await host.ReadKey(cancelled.Token)).Throws<OperationCanceledException>();
        terminal.Type("c");
        var afterCancellation = await host.ReadKey(cancellationToken);

        _ = await Assert.That(first).IsEqualTo(new TerminalKey(TerminalKeyKind.Character, "a"));
        _ = await Assert.That(afterCancellation).IsEqualTo(new TerminalKey(TerminalKeyKind.Character, "c"));
    }

    [Test]
    public async Task Live_input_reset_discards_keys_buffered_for_the_previous_owner(CancellationToken cancellationToken)
    {
        using var terminal = new ScriptedTerminal(80);
        var host = new EnhancedLiveInputHost(terminal, static (_, _) => Task.CompletedTask);
        terminal.Type("ab");
        var first = await host.ReadKey(cancellationToken);

        host.ResetInput();
        terminal.Type("c");
        var afterReset = await host.ReadKey(cancellationToken);

        _ = await Assert.That(first).IsEqualTo(new TerminalKey(TerminalKeyKind.Character, "a"));
        _ = await Assert.That(afterReset).IsEqualTo(new TerminalKey(TerminalKeyKind.Character, "c"));
    }

    [Test]
    public async Task Live_input_cancellation_discards_partial_decoder_state(CancellationToken cancellationToken)
    {
        using var terminal = new ScriptedTerminal(80);
        var host = new EnhancedLiveInputHost(terminal, static (_, _) => Task.CompletedTask);
        using var cancelled = new CancellationTokenSource();
        terminal.Type("\u001b");
        var pending = host.ReadKey(cancelled.Token).AsTask();
        await cancelled.CancelAsync();

        _ = await Assert.That(async () => await pending.WaitAsync(CancellationToken.None))
            .Throws<OperationCanceledException>();
        terminal.Type("[A");
        var afterCancellation = await host.ReadKey(cancellationToken);

        _ = await Assert.That(afterCancellation).IsEqualTo(new TerminalKey(TerminalKeyKind.Character, "["));
    }

    [Test]
    public async Task Decoder_maps_tab_to_completion()
    {
        var decoded = new TerminalKeyDecoder().Feed([0x09]);

        _ = await Assert.That(decoded).HasSingleItem();
        _ = await Assert.That(decoded[0]).IsEqualTo(new TerminalKey(TerminalKeyKind.Complete));
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
    [Arguments("\u001b[13;2u")]
    [Arguments("\u001b[27;2;13~")]
    public async Task Shift_enter_inserts_a_newline_and_enter_submits(string shiftEnter)
    {
        var decoder = new TerminalKeyDecoder();
        var editor = new IncrementalEditor("> ", 64 * 1024);
        var decoded = decoder.Feed(Encoding.UTF8.GetBytes($"first{shiftEnter}second\r"));
        string? submitted = null;

        foreach (var key in decoded)
        {
            submitted = editor.Apply(key) ?? submitted;
        }

        _ = await Assert.That(submitted).IsEqualTo("first\nsecond");
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
    public async Task Editor_replaces_text_rune_aware_at_its_input_limit()
    {
        var editor = new IncrementalEditor("> ", 3);

        editor.Replace("a🙂b\u001b[2Jc");

        _ = await Assert.That(editor.Prompt).IsEqualTo(new PromptValue("> ", "a🙂b", 3));
    }

    [Test]
    [Arguments("one\ntwo", 80, "one|two")]
    [Arguments("abcdef", 3, "abc|def")]
    [Arguments("a界b", 3, "a界|b")]
    public async Task Text_layout_preserves_newlines_and_wraps_at_cell_width(
        string value, int width, string expected)
    {
        var rows = TerminalText.Layout(value, width);

        _ = await Assert.That(string.Join('|', rows)).IsEqualTo(expected);
    }

    [Test]
    public async Task Marquee_keeps_streamed_text_on_one_row_and_moves_it_left()
    {
        var context = new LiveBufferRenderContext(5, new TerminalPalette(false));
        var first = new MarqueeValue("● ", "abcdef", 0).Render(context);
        var next = new MarqueeValue("● ", "abcdef", 1).Render(context);
        var stationary = new MarqueeValue("● ", "a\nb", 20).Render(context);
        var wide = new MarqueeValue(string.Empty, "界ab", 1).Render(new LiveBufferRenderContext(2, context.Palette));
        var joined = new MarqueeValue(string.Empty, "👨‍👩‍👧‍👦a", 0).Render(new LiveBufferRenderContext(2, context.Palette));

        _ = await Assert.That(first.Lines).Count().IsEqualTo(1);
        _ = await Assert.That(first.Lines[0].Text).IsEqualTo("● abc");
        _ = await Assert.That(next.Lines[0].Text).IsEqualTo("● bcd");
        _ = await Assert.That(stationary.Lines[0].Text).IsEqualTo("● a b");
        _ = await Assert.That(wide.Lines[0].Text).IsEqualTo(" a");
        _ = await Assert.That(joined.Lines[0].Text).IsEqualTo("👨‍👩‍👧‍👦");
        _ = await Assert.That(first.Retention).IsEqualTo(LiveBufferRetention.Tail);
    }

    [Test]
    public async Task Live_buffer_values_render_rich_multiline_results()
    {
        var context = new LiveBufferRenderContext(4, new TerminalPalette(false));
        var prompt = new PromptValue("> ", "a界\nb", 3).Render(context);
        var modeline = new ModelineValue("chat", string.Empty, "model").Render(context);
        var spinner = new SpinnerValue("work\u001b[2J", 0).Render(context);
        var text = new LiveTextValue("one\ntwo").Render(context);

        _ = await Assert.That(string.Join('|', prompt.Lines.Select(value => value.Text))).IsEqualTo("> a|  界|  b");
        _ = await Assert.That(prompt.Caret).IsEqualTo(new LiveBufferCaret(2, 2));
        _ = await Assert.That(prompt.Retention).IsEqualTo(LiveBufferRetention.Caret);
        _ = await Assert.That(modeline.Retention).IsEqualTo(LiveBufferRetention.Fixed);
        _ = await Assert.That(spinner.Lines[0].Text).IsEqualTo("⠋ work[2J");
        _ = await Assert.That(text.Lines.Count).IsEqualTo(2);
        _ = await Assert.That(text.Retention).IsEqualTo(LiveBufferRetention.Tail);
    }

    [Test]
    [Arguments("chat", "working", "provider/model", 34, "─ mode: chat ─ wo provider/model ")]
    [Arguments("chat", "", "provider/model", 8, "─ provi")]
    [Arguments("chat", "idle", "", 4, "─ ─")]
    public async Task Modeline_aligns_or_clips_terminal_safe_values(
        string mode, string activity, string model, int width, string expected)
    {
        var rendered = new ModelineValue(mode, activity, model).Render(width);

        _ = await Assert.That(rendered).IsEqualTo(expected);
        _ = await Assert.That(TerminalText.Width(rendered)).IsLessThanOrEqualTo(width);
    }
}
