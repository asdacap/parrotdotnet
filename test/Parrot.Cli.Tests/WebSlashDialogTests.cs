using System.Threading.Channels;
using Parrot.Cli.Commands;
using Parrot.Cli.Web;
using Parrot.Web.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class WebSlashDialogTests
{
    private static readonly SlashDialogOption[] Options =
    [
        new("first", "First", "the first option"),
        new("second", "Second", "the second option"),
    ];

    [Test]
    [Arguments("select", "option:second", SlashFrame.PayloadOneofCase.Select, "second")]
    [Arguments("select", "dismissed", SlashFrame.PayloadOneofCase.Select, null)]
    [Arguments("read_text", "text:typed", SlashFrame.PayloadOneofCase.ReadText, "typed")]
    [Arguments("read_secret", "dismissed", SlashFrame.PayloadOneofCase.ReadText, null)]
    [Arguments("confirm", "confirmed", SlashFrame.PayloadOneofCase.Confirm, "True")]
    [Arguments("confirm", "dismissed", SlashFrame.PayloadOneofCase.Confirm, "False")]
    [Arguments("show", "dismissed", SlashFrame.PayloadOneofCase.Show, "shown")]
    public async Task Prompt_emits_a_frame_and_resolves_with_the_browser_answer(
        string method, string answer, SlashFrame.PayloadOneofCase expectedFrame, string? expectedResult, CancellationToken cancellationToken)
    {
        var frames = Channel.CreateUnbounded<SlashFrame>();
        var dialog = new WebSlashDialog(frames.Writer);

        Func<Task<string?>> prompt = method switch
        {
            "select" => async () => (await dialog.Select("Pick", Options, cancellationToken))?.Id,
            "read_text" => () => dialog.ReadText("Name", cancellationToken),
            "read_secret" => () => dialog.ReadSecret("Key", cancellationToken),
            "confirm" => async () => (await dialog.Confirm(["Sure?"], cancellationToken)).ToString(),
            "show" => () => Show(dialog, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
        var result = prompt();
        var frame = await frames.Reader.ReadAsync(cancellationToken);
        var promptId = frame.PayloadCase switch
        {
            SlashFrame.PayloadOneofCase.Select => frame.Select.PromptId,
            SlashFrame.PayloadOneofCase.ReadText => frame.ReadText.PromptId,
            SlashFrame.PayloadOneofCase.Confirm => frame.Confirm.PromptId,
            SlashFrame.PayloadOneofCase.Show => frame.Show.PromptId,
            _ => string.Empty,
        };
        var answered = dialog.Answer(Answer(promptId, answer));

        _ = await Assert.That(frame.PayloadCase).IsEqualTo(expectedFrame);
        _ = await Assert.That(answered).IsTrue();
        _ = await Assert.That(await result).IsEqualTo(expectedResult);
        _ = await Assert.That(dialog.Answer(Answer(promptId, answer))).IsFalse();
    }

    private static AnswerSlashPromptRequest Answer(string promptId, string answer) =>
        answer.Split(':', 2) switch
        {
            ["option", var optionId] => new() { PromptId = promptId, OptionId = optionId },
            ["text", var text] => new() { PromptId = promptId, Text = text },
            ["confirmed"] => new() { PromptId = promptId, Confirmed = true },
            _ => new() { PromptId = promptId, Dismissed = true },
        };

    private static async Task<string?> Show(WebSlashDialog dialog, CancellationToken cancellationToken)
    {
        await dialog.Show(["Shown"], cancellationToken);
        return "shown";
    }
}
