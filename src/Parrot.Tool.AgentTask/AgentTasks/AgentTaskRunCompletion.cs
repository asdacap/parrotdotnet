using Parrot.Agent;
using Parrot.Config;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.AgentTasks;

internal sealed class AgentTaskRunCompletion(
    IAgentSession session,
    ToolOutputBlobStore outputBlobs,
    IPromptTemplateCatalog promptTemplates) : IAgentTaskRunCompletion
{
    private readonly Dictionary<string, Task<string>> _messages = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task Deliver(AgentTaskRunTerminal terminal, CancellationToken cancellationToken) =>
        DeliverCore(terminal, wakeCaller: true, cancellationToken);

    public Task DeliverDuringShutdown(AgentTaskRunTerminal terminal, CancellationToken cancellationToken) =>
        DeliverCore(terminal, wakeCaller: false, cancellationToken);

    private async Task DeliverCore(
        AgentTaskRunTerminal terminal,
        bool wakeCaller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        Task<string> messageTask;
        lock (_gate)
        {
            if (_messages.TryGetValue(terminal.CompletionMessageId, out var preparedMessage)
                && !preparedMessage.IsFaulted)
            {
                messageTask = preparedMessage;
            }
            else
            {
                messageTask = PrepareMessage(terminal);
                _messages[terminal.CompletionMessageId] = messageTask;
                _ = messageTask.ContinueWith(
                    _ => RemoveFaultedMessage(terminal.CompletionMessageId, messageTask),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        var message = await messageTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (wakeCaller)
        {
            _ = await session.Send(
                [ConversationPart.TextPart(message)],
                terminal.CompletionMessageId,
                Delivery.Steer,
                new IncomingActivity(terminal.RunId, $"AgentTask graph {terminal.RunId} completion"),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await session.Record(
                [ConversationPart.TextPart(message)],
                terminal.CompletionMessageId,
                Delivery.Steer,
                cancellationToken).ConfigureAwait(false);
        }

        RemovePreparedMessage(terminal.CompletionMessageId, messageTask);
    }

    private void RemovePreparedMessage(string messageId, Task<string> message)
    {
        lock (_gate)
        {
            if (_messages.TryGetValue(messageId, out var cached) && ReferenceEquals(cached, message))
            {
                _ = _messages.Remove(messageId);
            }
        }
    }

    private void RemoveFaultedMessage(string messageId, Task<string> message)
    {
        _ = message.Exception;
        lock (_gate)
        {
            if (_messages.TryGetValue(messageId, out var cached) && ReferenceEquals(cached, message))
            {
                _ = _messages.Remove(messageId);
            }
        }
    }

    private async Task<string> PrepareMessage(AgentTaskRunTerminal terminal)
    {
        var outcome = terminal.Result.Length == 0 ? terminal.Error : terminal.Result;
        if (ToolOutputBlobStore.IsOversized(outcome))
        {
            outcome = await outputBlobs.Persist(outcome, CancellationToken.None).ConfigureAwait(false);
        }

        return promptTemplates.Render(
            "agent-task-run.completion",
            [
                new PromptTemplateArgument("run_id", terminal.RunId),
                new PromptTemplateArgument("status", terminal.Status.ToString().ToLowerInvariant()),
                new PromptTemplateArgument("outcome", outcome),
            ]);
    }
}
