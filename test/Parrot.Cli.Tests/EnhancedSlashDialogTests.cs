using Parrot.Cli.Commands;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedSlashDialogTests
{
    [Test]
    public async Task Picker_filters_navigates_and_selects_through_live_items(CancellationToken cancellationToken)
    {
        var host = new ScriptedLiveInputHost("al", "\u001b[B", "\r");
        var dialog = new EnhancedSlashDialog(host);
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
        _ = await Assert.That(filtered).Count().IsEqualTo(3);
        _ = await Assert.That(filtered[0]).IsEqualTo(new PromptValue("Filter: ", "al", 2));
        _ = await Assert.That(filtered[1]).IsEqualTo(new PickerOptionValue("Alpha", "first", true));
        _ = await Assert.That(filtered[2]).IsEqualTo(new PickerOptionValue("Alpine", "mountain", false));
        _ = await Assert.That(collapsed.Count).IsEqualTo(1);
        _ = await Assert.That(collapsed[0]).IsEqualTo(new PromptValue("Filter: ", "Alpine", 6));
    }

    [Test]
    public async Task Picker_places_prompt_before_no_matches(CancellationToken cancellationToken)
    {
        var host = new ScriptedLiveInputHost("z\u001b", string.Empty);
        var dialog = new EnhancedSlashDialog(host);

        _ = await dialog.Select(
            "Question: ",
            [new SlashDialogOption("one", "One", string.Empty)],
            cancellationToken);

        var unmatched = host.Frames[1];
        _ = await Assert.That(unmatched).Count().IsEqualTo(2);
        _ = await Assert.That(unmatched[0]).IsEqualTo(new PromptValue("Question: ", "z", 1));
        _ = await Assert.That(unmatched[1]).IsEqualTo(new PickerOptionValue("No matches", string.Empty, false));
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
        var dialog = new EnhancedSlashDialog(host);

        var selected = await dialog.Select(
            "Pick: ",
            [new SlashDialogOption("one", "One", string.Empty)],
            cancellationToken);

        _ = await Assert.That(selected).IsNull();
    }

    [Test]
    public async Task Confirmation_reports_escape_as_cancellation(CancellationToken cancellationToken)
    {
        var dialog = new EnhancedSlashDialog(new ScriptedLiveInputHost("\u001b", string.Empty));

        var confirmed = await dialog.Confirm(["Continue"], cancellationToken);

        _ = await Assert.That(confirmed).IsFalse();
    }

    [Test]
    public async Task Text_secret_and_information_are_all_live_input_items(CancellationToken cancellationToken)
    {
        var textHost = new ScriptedLiveInputHost("hello\r");
        var textDialog = new EnhancedSlashDialog(textHost);
        var text = await textDialog.ReadText("Name: ", cancellationToken);

        var secretHost = new ScriptedLiveInputHost("sëcret\r", "\r", "\r");
        var secretDialog = new EnhancedSlashDialog(secretHost);
        var secret = await secretDialog.ReadSecret("Key: ", cancellationToken);

        await secretDialog.Show(["first", "second"], cancellationToken);
        await secretDialog.ShowError("failed", cancellationToken);

        _ = await Assert.That(text).IsEqualTo("hello");
        _ = await Assert.That(textHost.Frames[^1].Count).IsEqualTo(1);
        _ = await Assert.That(textHost.Frames[^1][0]).IsEqualTo(new PromptValue("Name: ", "hello", 5));
        _ = await Assert.That(secret).IsEqualTo("sëcret");
        _ = await Assert.That(secretHost.Frames.SelectMany(frame => frame).OfType<PromptValue>())
            .DoesNotContain(value => value.Text.Contains("sëcret", StringComparison.Ordinal));
        _ = await Assert.That(secretHost.Frames[^2][0]).IsEqualTo(new DialogMessageValue("first\nsecond", false));
        _ = await Assert.That(secretHost.Frames[^1][0]).IsEqualTo(new DialogMessageValue("failed", true));
    }
}
