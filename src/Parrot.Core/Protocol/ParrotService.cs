using System.Collections.Concurrent;
using Grpc.Core;
using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;

namespace Parrot.Protocol;

// The gRPC contract. Commands in, one flat event stream out.
internal sealed class ParrotService(ILLMProvider provider) : Parrot.ParrotBase
{
    private readonly ConcurrentDictionary<string, SessionHost> _sessions = new(StringComparer.Ordinal);

    public override Task<SendMessageResponse> SendMessage(SendMessageRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var host = Host(request.SessionId);
        var taskId = Identifier.New();

        // Admitting the prompt does not wait for the turn, and does not require
        // anyone to be listening.
        host.Turn = host.Session.Run(taskId, request.Model, request.Text, context.CancellationToken);

        return Task.FromResult(new SendMessageResponse { MessageId = Identifier.New(), TaskId = taskId });
    }

    public override async Task Listen(
        ListenRequest request,
        IServerStreamWriter<Event> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var host = Host(request.SessionId);

        await foreach (var published in host.Events.Subscribe(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(published, context.CancellationToken).ConfigureAwait(false);
        }

        await host.Turn.ConfigureAwait(false);
    }

    private SessionHost Host(string sessionId) =>
        _sessions.GetOrAdd(
            sessionId,
            id =>
            {
                var events = new EventBroker();
                return new SessionHost(new AgentSession(id, provider, events), events);
            });
}
