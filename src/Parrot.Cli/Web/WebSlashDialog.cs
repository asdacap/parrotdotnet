using System.Collections.Concurrent;
using System.Threading.Channels;
using Parrot.Cli.Commands;
using Parrot.Web.Protocol;

namespace Parrot.Cli.Web;

internal sealed class WebSlashDialog(ChannelWriter<SlashFrame> frames) : ISlashDialog
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AnswerSlashPromptRequest>> _prompts = new();

    public bool Answer(AnswerSlashPromptRequest answer) =>
        _prompts.TryRemove(answer.PromptId, out var prompt) && prompt.TrySetResult(answer);

    public async Task<SlashDialogOption?> Select(
        string title, IReadOnlyList<SlashDialogOption> options, CancellationToken cancellationToken)
    {
        var answer = await Ask(
            promptId =>
            {
                var select = new SlashSelect { PromptId = promptId, Title = title };
                select.Options.AddRange(options.Select(static option =>
                    new SlashOption { Id = option.Id, Label = option.Label, Description = option.Description }));
                return new SlashFrame { Select = select };
            },
            cancellationToken).ConfigureAwait(false);
        return answer.AnswerCase == AnswerSlashPromptRequest.AnswerOneofCase.OptionId
            ? options.FirstOrDefault(option => option.Match(answer.OptionId))
            : null;
    }

    public Task<string?> ReadText(string prompt, CancellationToken cancellationToken) =>
        Read(prompt, false, cancellationToken);

    public Task<string?> ReadSecret(string prompt, CancellationToken cancellationToken) =>
        Read(prompt, true, cancellationToken);

    public async Task Show(IReadOnlyList<string> lines, CancellationToken cancellationToken) =>
        _ = await Ask(
            promptId => new SlashFrame { Show = new SlashShow { PromptId = promptId, Lines = { lines } } },
            cancellationToken).ConfigureAwait(false);

    public async Task Print(IReadOnlyList<string> lines, CancellationToken cancellationToken) =>
        await frames.WriteAsync(new SlashFrame { Print = new SlashPrint { Lines = { lines } } }, cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> Confirm(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        var answer = await Ask(
            promptId => new SlashFrame { Confirm = new SlashConfirm { PromptId = promptId, Lines = { lines } } },
            cancellationToken).ConfigureAwait(false);
        return answer.AnswerCase == AnswerSlashPromptRequest.AnswerOneofCase.Confirmed && answer.Confirmed;
    }

    public async Task ShowError(string message, CancellationToken cancellationToken) =>
        await frames.WriteAsync(new SlashFrame { Error = new SlashError { Message = message } }, cancellationToken)
            .ConfigureAwait(false);

    public async Task<T> Load<T>(string activity, Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(load);
        await frames.WriteAsync(new SlashFrame { Loading = new SlashLoading { Activity = activity } }, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await load(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await frames.WriteAsync(new SlashFrame { Loaded = new SlashLoaded() }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> Read(string prompt, bool secret, CancellationToken cancellationToken)
    {
        var answer = await Ask(
            promptId => new SlashFrame { ReadText = new SlashReadText { PromptId = promptId, Prompt = prompt, Secret = secret } },
            cancellationToken).ConfigureAwait(false);
        return answer.AnswerCase == AnswerSlashPromptRequest.AnswerOneofCase.Text ? answer.Text : null;
    }

    private async Task<AnswerSlashPromptRequest> Ask(Func<string, SlashFrame> frame, CancellationToken cancellationToken)
    {
        var promptId = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<AnswerSlashPromptRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        _prompts[promptId] = answer;
        try
        {
            await frames.WriteAsync(frame(promptId), cancellationToken).ConfigureAwait(false);
            return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _prompts.TryRemove(promptId, out _);
        }
    }
}
