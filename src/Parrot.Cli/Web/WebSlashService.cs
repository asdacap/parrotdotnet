using System.Collections.Concurrent;
using System.Threading.Channels;
using Grpc.Core;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Protocol;
using Parrot.Web.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Web;

internal sealed class WebSlashService(
    GeneratedParrot.ParrotClient client,
    Configuration configuration,
    string workingDirectory,
    CancellationTokenSource applicationExit,
    ICredentialStore credentials,
    IOAuthClient oauth,
    IReadOnlyList<string> providerIds,
    IDiagnosticLog diagnostics) : ParrotWeb.ParrotWebBase
{
    private readonly ConcurrentDictionary<string, WebSlashDialog> _runs = new();

    public override Task<DescribeHostResponse> DescribeHost(DescribeHostRequest request, ServerCallContext context) =>
        Task.FromResult(new DescribeHostResponse { WorkingDirectory = workingDirectory });

    public override Task<ListSlashCommandsResponse> ListSlashCommands(
        ListSlashCommandsRequest request, ServerCallContext context)
    {
        var frames = Channel.CreateUnbounded<SlashFrame>();
        var registry = CreateRegistry(new WebSlashDialog(frames.Writer), new UserSession(), frames.Writer);
        var response = new ListSlashCommandsResponse();
        response.Commands.AddRange(registry.Commands.Select(static command =>
            new SlashCommand { Name = command.Name, Summary = command.Summary }));
        return Task.FromResult(response);
    }

    public override async Task RunSlashCommand(
        RunSlashCommandRequest request, IServerStreamWriter<SlashFrame> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var cancellationToken = context.CancellationToken;
        var runId = Guid.NewGuid().ToString("N");
        var frames = Channel.CreateUnbounded<SlashFrame>();
        var dialog = new WebSlashDialog(frames.Writer);
        _runs[runId] = dialog;
        try
        {
            await responseStream.WriteAsync(new SlashFrame { Started = new SlashStarted { RunId = runId } }, cancellationToken)
                .ConfigureAwait(false);
            var session = await client.AttachSessionAsync(
                new AttachSessionRequest { UserSessionId = request.UserSessionId, WorkingDirectory = workingDirectory },
                cancellationToken: cancellationToken);
            var dispatching = Dispatch(CreateRegistry(dialog, session, frames.Writer), request.Text, frames.Writer, cancellationToken);
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }

            await dispatching.ConfigureAwait(false);
        }
        finally
        {
            _ = _runs.TryRemove(runId, out _);
        }
    }

    public override Task<AnswerSlashPromptResponse> AnswerSlashPrompt(
        AnswerSlashPromptRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _runs.TryGetValue(request.RunId, out var dialog) && dialog.Answer(request)
            ? Task.FromResult(new AnswerSlashPromptResponse())
            : throw new RpcException(new Status(StatusCode.NotFound, "the slash prompt is not waiting for an answer"));
    }

    private static async Task Dispatch(
        SlashCommandRegistry registry, string text, ChannelWriter<SlashFrame> frames, CancellationToken cancellationToken)
    {
        try
        {
            await registry.Dispatch(text, cancellationToken).ConfigureAwait(false);
            frames.Complete();
        }
        catch (Exception failure) when (failure is not RpcException and not OperationCanceledException)
        {
            _ = frames.TryWrite(new SlashFrame { Error = new SlashError { Message = failure.Message } });
            frames.Complete();
        }
        catch (Exception failure)
        {
            frames.Complete(failure);
            throw;
        }
    }

    private SlashCommandRegistry CreateRegistry(WebSlashDialog dialog, UserSession session, ChannelWriter<SlashFrame> frames)
    {
        var slashSession = SlashSession.Create(
            client,
            session,
            configuration,
            true,
            new CliSlashSessionBinding((replaced, token) =>
                frames.WriteAsync(new SlashFrame { SessionReplaced = replaced }, token).AsTask()));

        // The browser dispatches a slash command only while the session is idle.
        return SlashCommands.Create(
            client,
            dialog,
            slashSession,
            new SlashActivity(static () => false),
            new WebApplicationExit(frames, applicationExit),
            credentials,
            oauth,
            providerIds,
            static _ => Task.CompletedTask,
            diagnostics);
    }
}
