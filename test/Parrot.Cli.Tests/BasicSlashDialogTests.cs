using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class BasicSlashDialogTests
{
    [Test]
    [Arguments("wrong\nbuild\n", "build", true)]
    [Arguments("2\n", "review", false)]
    [Arguments("\n", null, false)]
    [Arguments("", null, false)]
    public async Task Selects_by_number_or_id_retries_invalid_input_and_dismisses_blank_or_end_of_input(
        string input,
        string? expected,
        bool reportsInvalidInput,
        CancellationToken cancellationToken)
    {
        var options = new[]
        {
            new SlashDialogOption("build", "Build", "Implement a change"),
            new SlashDialogOption("review", "Review", "Inspect a change"),
        };
        using var reader = new StringReader(input);
        using var output = new StringWriter();
        using var error = new StringWriter();
        ISlashDialog dialog = new BasicSlashDialog(reader, output, error);

        var selected = await dialog.Select("Choose a mode", options, cancellationToken);

        _ = await Assert.That(selected?.Id).IsEqualTo(expected);
        _ = await Assert.That(output.ToString()).Contains("Choose a mode");
        _ = await Assert.That(output.ToString()).Contains("1. Build — Implement a change");
        _ = await Assert.That(error.ToString()).IsEqualTo(reportsInvalidInput
            ? $"Choose a listed number or id.{Environment.NewLine}"
            : string.Empty);
    }

    [Test]
    [Arguments("y\n", true)]
    [Arguments(" YES \n", true)]
    [Arguments("n\n", false)]
    [Arguments("\n", false)]
    [Arguments("", false)]
    public async Task Confirmation_shows_message_and_accepts_only_yes(
        string input,
        bool expected,
        CancellationToken cancellationToken)
    {
        using var reader = new StringReader(input);
        using var output = new StringWriter();
        using var error = new StringWriter();
        ISlashDialog dialog = new BasicSlashDialog(reader, output, error);

        var confirmed = await dialog.Confirm(["Continue"], cancellationToken);

        _ = await Assert.That(confirmed).IsEqualTo(expected);
        _ = await Assert.That(output.ToString()).IsEqualTo(
            $"Continue{Environment.NewLine}Continue? [y/N]{Environment.NewLine}> ");
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task Text_secret_show_and_error_use_the_supplied_streams(CancellationToken cancellationToken)
    {
        using var input = new StringReader("answer\nsecret\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        ISlashDialog dialog = new BasicSlashDialog(input, output, error);

        var text = await dialog.ReadText("Question", cancellationToken);
        var secret = await dialog.ReadSecret("Key", cancellationToken);
        await dialog.Show(["first", "second"], cancellationToken);
        await dialog.ShowError("failure", cancellationToken);

        _ = await Assert.That(text).IsEqualTo("answer");
        _ = await Assert.That(secret).IsEqualTo("secret");
        _ = await Assert.That(output.ToString()).IsEqualTo(
            $"Question{Environment.NewLine}> Key{Environment.NewLine}> first{Environment.NewLine}second{Environment.NewLine}");
        _ = await Assert.That(output.ToString()).DoesNotContain("secret");
        _ = await Assert.That(error.ToString()).IsEqualTo($"failure{Environment.NewLine}");
    }
}
