using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class BasicSlashDialogTests
{
    [Test]
    public async Task Selects_by_number_or_id_retries_invalid_input_and_dismisses_blank_or_end_of_input(
        CancellationToken cancellationToken)
    {
        var options = new[]
        {
            new SlashDialogOption("build", "Build", "Implement a change"),
            new SlashDialogOption("review", "Review", "Inspect a change"),
        };

        foreach (var (input, expected) in new[]
        {
            ("wrong\nbuild\n", "build"),
            ("2\n", "review"),
            ("\n", (string?)null),
            (string.Empty, (string?)null),
        })
        {
            using var reader = new StringReader(input);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var dialog = new BasicSlashDialog(reader, output, error);

            var selected = await dialog.Select("Choose a mode", options, cancellationToken);

            _ = await Assert.That(selected?.Id).IsEqualTo(expected);
            _ = await Assert.That(output.ToString()).Contains("Choose a mode");
            _ = await Assert.That(output.ToString()).Contains("1. Build — Implement a change");
            _ = await Assert.That(error.ToString()).IsEqualTo(
                expected == "build" ? $"Choose a listed number or id.{Environment.NewLine}" : string.Empty);
        }
    }

    [Test]
    public async Task Text_secret_show_and_error_use_the_supplied_streams(CancellationToken cancellationToken)
    {
        using var input = new StringReader("answer\nsecret\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var dialog = new BasicSlashDialog(input, output, error);

        var text = await dialog.ReadText("Question", cancellationToken);
        var secret = await dialog.ReadSecret("Key", cancellationToken);
        await dialog.Show(["first", "second"], cancellationToken);
        await dialog.ShowError("failure", cancellationToken);

        _ = await Assert.That(text).IsEqualTo("answer");
        _ = await Assert.That(secret).IsEqualTo("secret");
        _ = await Assert.That(output.ToString()).IsEqualTo(
            $"Question: Key: first{Environment.NewLine}second{Environment.NewLine}");
        _ = await Assert.That(output.ToString()).DoesNotContain("secret");
        _ = await Assert.That(error.ToString()).IsEqualTo($"failure{Environment.NewLine}");
    }
}
