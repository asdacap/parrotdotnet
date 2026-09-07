using Parrot.Cli.Commands;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedSlashDialogTests
{
    [Test]
    public async Task Picker_filters_navigates_and_selects_through_live_items(CancellationToken cancellationToken)
    {
        var host = new ScriptedLiveInputHost("al", "\u001b[B", "\r");
        ISlashDialog dialog = new EnhancedSlashDialog(host);
        var options = new SlashDialogOption[]
        {
            new("alpha", "Alpha", "first"),
            new("alpine", "Alpine", "mountain"),
            new("beta", "Beta", "second"),
        };

        var selected = await dialog.Select("Filter: ", options, cancellationToken);
        var filtered = host.Frames[2];
        var collapsed = host.Frames[^1];

        _ = await Assert.That(selected).IsSameReferenceAs(options[1]);
        _ = await Assert.That(filtered).Count().IsEqualTo(4);
        _ = await Assert.That(filtered[0]).IsEqualTo(new LiveTextValue("Filter: "));
        await AssertPrompt(filtered[1], "> al", 4);
        _ = await Assert.That(filtered[2]).IsEqualTo(new PickerOptionValue("Alpha", "first", true));
        _ = await Assert.That(filtered[3]).IsEqualTo(new PickerOptionValue("Alpine", "mountain", false));
        _ = await Assert.That(collapsed.Count).IsEqualTo(2);
        _ = await Assert.That(collapsed[0]).IsEqualTo(new LiveTextValue("Filter: "));
        await AssertPrompt(collapsed[1], "> Alpine", 8);
    }

    [Test]
    public async Task Picker_places_prompt_before_no_matches(CancellationToken cancellationToken)
    {
        var host = new ScriptedLiveInputHost("z\u001b", string.Empty);
        ISlashDialog dialog = new EnhancedSlashDialog(host);

        _ = await dialog.Select(
            "Question: ",
            [new SlashDialogOption("one", "One", string.Empty)],
            cancellationToken);

        var unmatched = host.Frames[1];
        _ = await Assert.That(unmatched).Count().IsEqualTo(3);
        _ = await Assert.That(unmatched[0]).IsEqualTo(new LiveTextValue("Question: "));
        await AssertPrompt(unmatched[1], "> z", 3);
        _ = await Assert.That(unmatched[2]).IsEqualTo(new PickerOptionValue("No matches", string.Empty, false));
    }

    [Test]
    [Arguments("\u001b", "")]
    [Arguments("\u0003", null)]
    [Arguments("\u0004", null)]
    public async Task Picker_cancels_on_escape_interrupt_or_end_of_file(
        string key,
        string? flush,
        CancellationToken cancellationToken)
    {
        var host = flush is null ? new ScriptedLiveInputHost(key) : new ScriptedLiveInputHost(key, flush);
        ISlashDialog dialog = new EnhancedSlashDialog(host);

        var selected = await dialog.Select(
            "Pick: ",
            [new SlashDialogOption("one", "One", string.Empty)],
            cancellationToken);

        _ = await Assert.That(selected).IsNull();
    }

    [Test]
    public async Task Picker_does_not_interpret_a_key_returned_during_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new CancellingLiveInputHost(cancellation, new TerminalKey(TerminalKeyKind.Submit));
        ISlashDialog dialog = new EnhancedSlashDialog(host);

        _ = await Assert.That(async () => await dialog.Select(
            "Pick: ",
            [new SlashDialogOption("one", "One", string.Empty)],
            cancellation.Token)).Throws<OperationCanceledException>();

        _ = await Assert.That(host.ReplaceCount).IsEqualTo(1);
    }

    [Test]
    public async Task Text_does_not_interpret_a_key_returned_during_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new CancellingLiveInputHost(cancellation, new TerminalKey(TerminalKeyKind.Character, "x"));
        ISlashDialog dialog = new EnhancedSlashDialog(host);

        _ = await Assert.That(async () => await dialog.ReadText("Name: ", cancellation.Token))
            .Throws<OperationCanceledException>();

        _ = await Assert.That(host.ReplaceCount).IsEqualTo(1);
    }

    [Test]
    public async Task Confirmation_reports_escape_as_cancellation(CancellationToken cancellationToken)
    {
        ISlashDialog dialog = new EnhancedSlashDialog(new ScriptedLiveInputHost("\u001b", string.Empty));

        var confirmed = await dialog.Confirm(["Continue"], cancellationToken);

        _ = await Assert.That(confirmed).IsFalse();
    }

    [Test]
    [Arguments("sëcret", 6)]
    [Arguments("sëcret🙂", 7)]
    public async Task Text_secret_and_information_are_all_live_input_items(
        string secretText, int secretRunes, CancellationToken cancellationToken)
    {
        var textHost = new ScriptedLiveInputHost("hello\r");
        ISlashDialog textDialog = new EnhancedSlashDialog(textHost);
        var text = await textDialog.ReadText("Name: ", cancellationToken);

        var secretHost = new ScriptedLiveInputHost($"{secretText}\u001b[D\r", "\r", "\r");
        ISlashDialog secretDialog = new EnhancedSlashDialog(secretHost);
        var secret = await secretDialog.ReadSecret("Key: ", cancellationToken);

        await secretDialog.Show(["first", "second"], cancellationToken);
        await secretDialog.ShowError("failed", cancellationToken);

        _ = await Assert.That(text).IsEqualTo("hello");
        _ = await Assert.That(textHost.Frames[^1].Count).IsEqualTo(2);
        _ = await Assert.That(textHost.Frames[^1][0]).IsEqualTo(new LiveTextValue("Name: "));
        await AssertPrompt(textHost.Frames[^1][1], "> hello", 7);
        _ = await Assert.That(secret).IsEqualTo(secretText);
        var context = new LiveBufferRenderContext(80, new TerminalPalette(false));
        _ = await Assert.That(secretHost.Frames.Select(frame => string.Join('\n', frame
                .SelectMany(item => item.Render(context).Lines).Select(line => line.Text))))
            .DoesNotContain(value => value.Contains(secretText, StringComparison.Ordinal));
        await AssertPrompt(secretHost.Frames[^4][1], "> " + new string('*', secretRunes), secretRunes + 1);
        await AssertPrompt(secretHost.Frames[^3][1], "> " + new string('*', secretRunes), secretRunes + 2);
        _ = await Assert.That(secretHost.Frames[0][0]).IsEqualTo(new LiveTextValue("Key: "));
        await AssertPrompt(secretHost.Frames[0][1], "> ", 2);
        _ = await Assert.That(secretHost.Frames[^2][0]).IsEqualTo(new DialogMessageValue("first\nsecond", false));
        _ = await Assert.That(secretHost.Frames[^1][0]).IsEqualTo(new DialogMessageValue("failed", true));
    }

    [Test]
    public async Task Load_shows_spinner_frames_returns_the_result_and_clears_the_input(CancellationToken cancellationToken)
    {
        var host = new ScriptedLiveInputHost();
        var dialog = new EnhancedSlashDialog(host);

        var loaded = await dialog.Load(
            "Loading models…",
            token => Task.FromResult(new SlashDialogOption("id", "Label", "description")),
            cancellationToken);

        _ = await Assert.That(loaded.Id).IsEqualTo("id");
        var frames = host.Frames;
        _ = await Assert.That(frames.Count).IsGreaterThanOrEqualTo(2);
        _ = await Assert.That(frames[0][0]).IsEqualTo(new PromptValue("> ", string.Empty, 0));
        _ = await Assert.That(frames[0][1]).IsEqualTo(new SpinnerValue("Loading models…", 0));
        _ = await Assert.That(frames[^1]).Count().IsEqualTo(1);
        _ = await Assert.That(frames[^1][0]).IsEqualTo(new PromptValue("> ", string.Empty, 0));
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Load_propagates_failure_and_still_clears_the_input(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var host = new ScriptedLiveInputHost();
        var dialog = new EnhancedSlashDialog(host);

        _ = await Assert.That(async () => await dialog.Load<string>(
            "Loading models…",
            async token =>
            {
                if (cancel)
                {
                    await cancellation.CancelAsync();
                }

                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("load failed");
            },
            cancellation.Token)).Throws<Exception>();

        _ = await Assert.That(host.Frames[^1]).Count().IsEqualTo(1);
        _ = await Assert.That(host.Frames[^1][0]).IsEqualTo(new PromptValue("> ", string.Empty, 0));
    }

    [Test]
    public async Task Test_dialog_load_records_the_activity_and_returns_the_result(CancellationToken cancellationToken)
    {
        var dialog = new TestSlashDialog();

        var loaded = await dialog.Load(
            "Loading models…",
            token => Task.FromResult("result"),
            cancellationToken);

        _ = await Assert.That(loaded).IsEqualTo("result");
        _ = await Assert.That(dialog.Loads).Count().IsEqualTo(1);
        _ = await Assert.That(dialog.Loads[0]).IsEqualTo("Loading models…");
    }

    private static async Task AssertPrompt(ILiveBufferItem item, string text, int column)
    {
        var rendered = item.Render(new LiveBufferRenderContext(80, new TerminalPalette(false)));

        _ = await Assert.That(rendered.Lines).HasSingleItem();
        _ = await Assert.That(rendered.Lines[0].Text).IsEqualTo(text);
        _ = await Assert.That(rendered.Caret).IsEqualTo(new LiveBufferCaret(0, column));
        _ = await Assert.That(rendered.Retention).IsEqualTo(LiveBufferRetention.Caret);
    }

    private sealed class CancellingLiveInputHost(
        CancellationTokenSource cancellation,
        TerminalKey key) : ILiveInputHost
    {
        public int ReplaceCount { get; private set; }

        public async ValueTask<TerminalKey> ReadKey(CancellationToken cancellationToken)
        {
            await cancellation.CancelAsync();
            return key;
        }

        public Task ReplaceInput(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken)
        {
            ReplaceCount++;
            return Task.CompletedTask;
        }

        public void ResetInput()
        {
        }
    }
}
