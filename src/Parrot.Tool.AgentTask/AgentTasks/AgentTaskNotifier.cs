using System.Threading.Channels;
using Parrot.Agent;
using Parrot.Diagnostics;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.AgentTasks;

/// <summary>Delivers task notifications to the owning agent in order, recording without waking once settling.</summary>
internal sealed class AgentTaskNotifier(IAgentSessionScope ownerScope, ToolOutputBlobStore outputBlobs, IDiagnosticLog diagnostics)
{
    private const int MaximumDeliveryAttempts = 3;
    private static readonly TimeSpan DeliveryRetryDelay = TimeSpan.FromSeconds(1);
    private readonly Channel<Notification> _pending = Channel.CreateUnbounded<Notification>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private Task? _delivery;
    private volatile bool _settling;

    internal void Enqueue(string text, string cause, bool wake)
    {
        _ = _pending.Writer.TryWrite(new Notification(text, cause, wake));
        lock (_gate)
        {
            _delivery ??= Deliver();
        }
    }

    internal Task Settle()
    {
        _settling = true;
        _ = _pending.Writer.TryComplete();
        lock (_gate)
        {
            return _delivery ?? Task.CompletedTask;
        }
    }

    private async Task Deliver()
    {
        await Task.Yield();
        await foreach (var notification in _pending.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            var text = ToolOutputBlobStore.IsOversized(notification.Text)
                ? await outputBlobs.Persist(notification.Text, CancellationToken.None).ConfigureAwait(false)
                : notification.Text;
            var messageId = Identifier.MessageId();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (notification.Wake && !_settling)
                    {
                        _ = await ownerScope.Session.Send(
                            [ConversationPart.TextPart(text)],
                            messageId,
                            Delivery.Steer,
                            new IncomingActivity("agent-tasks", notification.Cause),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        await ownerScope.Session.Record(
                            [ConversationPart.TextPart(text)],
                            messageId,
                            Delivery.Steer,
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    break;
                }
                catch (Exception) when (attempt < MaximumDeliveryAttempts)
                {
                    await Task.Delay(DeliveryRetryDelay, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    diagnostics.Write(new DiagnosticEvent("task_notification", "delivery", DiagnosticSeverity.Error)
                    {
                        AgentSessionId = ownerScope.Session.SessionId,
                        Outcome = "failed",
                        ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
                    });
                    break;
                }
            }
        }
    }

    private sealed record Notification(string Text, string Cause, bool Wake);
}
