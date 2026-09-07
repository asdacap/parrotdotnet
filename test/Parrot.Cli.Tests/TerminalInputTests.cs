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
        ILiveInputHost host = new EnhancedLiveInputHost(terminal, static (_, _) => Task.CompletedTask);
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
        ILiveInputHost host = new EnhancedLiveInputHost(terminal, static (_, _) => Task.CompletedTask);
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
        ILiveInputHost host = new EnhancedLiveInputHost(terminal, static (_, _) => Task.CompletedTask);
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
    public async Task Control_a_moves_to_the_start_of_the_prompt_while_home_moves_to_the_start_of_the_line()
    {
        var editor = new IncrementalEditor("> ", 64 * 1024);
        editor.Replace("first\nsecond");

        _ = editor.Apply(new TerminalKey(TerminalKeyKind.Home));
        var afterHome = editor.Prompt;
        var decoded = new TerminalKeyDecoder().Feed([0x01]);
        _ = editor.Apply(decoded[0]);

        _ = await Assert.That(decoded).HasSingleItem();
        _ = await Assert.That(decoded[0]).IsEqualTo(new TerminalKey(TerminalKeyKind.PromptStart));
        _ = await Assert.That(afterHome.Cursor).IsEqualTo(6);
        _ = await Assert.That(editor.Prompt.Cursor).IsEqualTo(0);
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
        _ = await Assert.That(editor.Prompt).IsEqualTo(new PromptState("> ", string.Empty, 0));
    }

    [Test]
    public async Task Editor_replaces_text_rune_aware_at_its_input_limit()
    {
        var editor = new IncrementalEditor("> ", 3);

        editor.Replace("a🙂b\u001b[2Jc");

        _ = await Assert.That(editor.Prompt).IsEqualTo(new PromptState("> ", "a🙂b", 3));
    }

    [Test]
    public async Task Editor_replaces_only_a_rune_range_and_leaves_the_caret_before_the_suffix()
    {
        var editor = new IncrementalEditor("> ", 20);
        editor.Replace("🙂 $al suffix");

        editor.ReplaceRange(2, 3, "$alpha");

        _ = await Assert.That(editor.Prompt).IsEqualTo(new PromptState("> ", "🙂 $alpha suffix", 8));
    }

    [Test]
    public async Task Editor_recalls_submitted_entries_up_from_an_empty_prompt_and_edits_away_from_them()
    {
        var editor = new IncrementalEditor("> ", 64 * 1024);
        editor.Remember("first");
        editor.Remember("second");
        editor.Remember("   ");

        editor.Recall();
        var mostRecent = editor.Prompt;
        editor.Recall();
        var oldest = editor.Prompt;
        editor.Next();
        var next = editor.Prompt;
        _ = editor.Apply(new TerminalKey(TerminalKeyKind.Character, "x"));
        var edited = editor.Prompt;
        _ = editor.Apply(new TerminalKey(TerminalKeyKind.Character, "y"));
        editor.Recall();
        var recallAfterEdit = editor.Prompt;
        editor.Next();
        editor.Next();
        var backToDraft = editor.Prompt;

        _ = await Assert.That(mostRecent.Text).IsEqualTo("second");
        _ = await Assert.That(mostRecent.Cursor).IsEqualTo(6);
        _ = await Assert.That(oldest.Text).IsEqualTo("first");
        _ = await Assert.That(next.Text).IsEqualTo("second");
        _ = await Assert.That(edited.Text).IsEqualTo("secondx");
        _ = await Assert.That(recallAfterEdit.Text).IsEqualTo("second");
        _ = await Assert.That(backToDraft.Text).IsEqualTo("secondxy");

        var empty = new IncrementalEditor("> ", 64 * 1024);
        empty.Recall();
        _ = await Assert.That(empty.Prompt).IsEqualTo(new PromptState("> ", string.Empty, 0));
    }

    [Test]
    [Arguments(-1, 0, 2)]
    [Arguments(1, 1, 4)]
    [Arguments(99, 2, 5)]
    public async Task Prompt_rendering_sanitizes_a_snapshot_and_clamps_its_rune_cursor(
        int cursor, int sanitizedCursor, int column)
    {
        var state = new PromptState(">\n ", "🙂\u0001x", cursor);
        ILiveBufferItem value = new PromptValue(state);
        var rendered = value.Render(new LiveBufferRenderContext(80, new TerminalPalette(false)));

        _ = await Assert.That(state).IsEqualTo(new PromptState(">\n ", "🙂\u0001x", cursor));
        _ = await Assert.That(state.Sanitize()).IsEqualTo(new PromptState("> ", "🙂x", sanitizedCursor));
        _ = await Assert.That(rendered.Lines).HasSingleItem();
        _ = await Assert.That(rendered.Lines[0].Text).IsEqualTo("> 🙂x");
        _ = await Assert.That(rendered.Caret).IsEqualTo(new LiveBufferCaret(0, column));
        _ = await Assert.That(rendered.Retention).IsEqualTo(LiveBufferRetention.Caret);

        var editor = new IncrementalEditor(">\n ", 20);
        editor.Replace("🙂x");
        var captured = editor.Prompt;
        editor.Clear();

        _ = await Assert.That(captured).IsEqualTo(new PromptState(">\n ", "🙂x", 2));
        _ = await Assert.That(editor.Prompt).IsEqualTo(new PromptState(">\n ", string.Empty, 0));
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
        ILiveBufferItem firstValue = new MarqueeValue("● ", "abcdef", 0);
        var first = firstValue.Render(context);
        ILiveBufferItem nextValue = new MarqueeValue("● ", "abcdef", 1);
        var next = nextValue.Render(context);
        ILiveBufferItem stationaryValue = new MarqueeValue("● ", "a\nb", 20);
        var stationary = stationaryValue.Render(context);
        ILiveBufferItem wideValue = new MarqueeValue(string.Empty, "界ab", 1);
        var wide = wideValue.Render(new LiveBufferRenderContext(2, context.Palette));
        ILiveBufferItem joinedValue = new MarqueeValue(string.Empty, "👨‍👩‍👧‍👦a", 0);
        var joined = joinedValue.Render(new LiveBufferRenderContext(2, context.Palette));

        _ = await Assert.That(first.Lines).Count().IsEqualTo(1);
        _ = await Assert.That(first.Lines[0].Text).IsEqualTo("● abc");
        _ = await Assert.That(next.Lines[0].Text).IsEqualTo("● bcd");
        _ = await Assert.That(stationary.Lines[0].Text).IsEqualTo("● a b");
        _ = await Assert.That(wide.Lines[0].Text).IsEqualTo(" a");
        _ = await Assert.That(joined.Lines[0].Text).IsEqualTo("👨‍👩‍👧‍👦");
        _ = await Assert.That(first.Retention).IsEqualTo(LiveBufferRetention.Tail);
    }

    [Test]
    public async Task Streamed_response_keeps_the_latest_text_within_the_viewport()
    {
        var context = new LiveBufferRenderContext(5, new TerminalPalette(false));
        ILiveBufferItem shortTextValue = new StreamedResponseValue("● ", "one");
        var shortText = shortTextValue.Render(context);
        ILiveBufferItem nextCharacterValue = new StreamedResponseValue("● ", "one t");
        var nextCharacter = nextCharacterValue.Render(context);
        ILiveBufferItem incompleteWordValue = new StreamedResponseValue("● ", "one tw");
        var incompleteWord = incompleteWordValue.Render(context);
        ILiveBufferItem completedWordValue = new StreamedResponseValue("● ", "one two");
        var completedWord = completedWordValue.Render(context);
        ILiveBufferItem trailingWhitespaceValue = new StreamedResponseValue("● ", "one two ");
        var trailingWhitespace = trailingWhitespaceValue.Render(context);
        ILiveBufferItem nextWordValue = new StreamedResponseValue("● ", "one two n");
        var nextWord = nextWordValue.Render(context);
        ILiveBufferItem newlineValue = new StreamedResponseValue("● ", "one\ntwo");
        var newline = newlineValue.Render(context);

        _ = await Assert.That(shortText.Lines[0].Text).IsEqualTo("● one");
        _ = await Assert.That(nextCharacter.Lines[0].Text).IsEqualTo("● e t");
        _ = await Assert.That(incompleteWord.Lines[0].Text).IsEqualTo("●  tw");
        _ = await Assert.That(completedWord.Lines[0].Text).IsEqualTo("● two");
        _ = await Assert.That(trailingWhitespace.Lines[0].Text).IsEqualTo("● wo ");
        _ = await Assert.That(nextWord.Lines[0].Text).IsEqualTo("● o n");
        _ = await Assert.That(newline.Lines[0].Text).IsEqualTo("● two");
        _ = await Assert.That(shortText.Retention).IsEqualTo(LiveBufferRetention.Tail);
    }

    [Test]
    public async Task Streamed_response_clips_graphemes_at_terminal_cell_boundaries()
    {
        var palette = new TerminalPalette(false);
        ILiveBufferItem wideValue = new StreamedResponseValue(string.Empty, "old 界ab");
        var wide = wideValue.Render(new LiveBufferRenderContext(3, palette));
        ILiveBufferItem joinedBoundaryValue = new StreamedResponseValue(string.Empty, "old 👨‍👩‍👧‍👦a");
        var joinedBoundary = joinedBoundaryValue.Render(new LiveBufferRenderContext(2, palette));
        ILiveBufferItem joinedTailValue = new StreamedResponseValue(string.Empty, "old a👨‍👩‍👧‍👦");
        var joinedTail = joinedTailValue.Render(new LiveBufferRenderContext(3, palette));
        ILiveBufferItem combiningValue = new StreamedResponseValue(string.Empty, "old e\u0301x");
        var combining = combiningValue.Render(new LiveBufferRenderContext(2, palette));
        ILiveBufferItem clippedPrefixValue = new StreamedResponseValue("● ", "response");
        var clippedPrefix = clippedPrefixValue.Render(new LiveBufferRenderContext(1, palette));

        _ = await Assert.That(wide.Lines[0].Text).IsEqualTo("ab");
        _ = await Assert.That(joinedBoundary.Lines[0].Text).IsEqualTo("a");
        _ = await Assert.That(joinedTail.Lines[0].Text).IsEqualTo("a👨‍👩‍👧‍👦");
        _ = await Assert.That(combining.Lines[0].Text).IsEqualTo("éx");
        _ = await Assert.That(clippedPrefix.Lines[0].Text).IsEqualTo("●");
        _ = await Assert.That(TerminalText.Width(wide.Lines[0].Text)).IsEqualTo(2);
        _ = await Assert.That(TerminalText.Width(joinedBoundary.Lines[0].Text)).IsEqualTo(1);
        _ = await Assert.That(TerminalText.Width(joinedTail.Lines[0].Text)).IsEqualTo(3);
    }

    [Test]
    public async Task Live_buffer_values_render_rich_multiline_results()
    {
        var context = new LiveBufferRenderContext(4, new TerminalPalette(false));
        ILiveBufferItem promptValue = new PromptValue("> ", "a界\nb", 3);
        var prompt = promptValue.Render(context);
        ILiveBufferItem modelineValue = new ModelineValue("chat", string.Empty, "model");
        var modeline = modelineValue.Render(context);
        ILiveBufferItem spinnerValue = new SpinnerValue("work\u001b[2J", 0);
        var spinner = spinnerValue.Render(context);
        ILiveBufferItem textValue = new LiveTextValue("one\ntwo");
        var text = textValue.Render(context);

        _ = await Assert.That(string.Join('|', prompt.Lines.Select(value => value.Text))).IsEqualTo("> a|  界|  b");
        _ = await Assert.That(prompt.Caret).IsEqualTo(new LiveBufferCaret(2, 2));
        _ = await Assert.That(prompt.Retention).IsEqualTo(LiveBufferRetention.Caret);
        _ = await Assert.That(modeline.Retention).IsEqualTo(LiveBufferRetention.Fixed);
        _ = await Assert.That(spinner.Lines[0].Text).IsEqualTo("⠋ wo");
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
