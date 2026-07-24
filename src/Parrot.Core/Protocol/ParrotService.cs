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

    public override async Task<ListModelsResponse> ListModels(ListModelsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var listed = await provider.ListModels(context.CancellationToken).ConfigureAwait(false);
        var response = new ListModelsResponse();

        foreach (var model in listed)
        {
            response.Models.Add(new Model { Id = model.Id, ProviderId = model.ProviderId });
        }

        return response;
    }

    public override Task<Session> CreateSession(CreateSessionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var host = Host(Identifier.New());
        host.Session.Model = request.Model;
        host.ParentSessionId = request.ParentSessionId;

        return Task.FromResult(Describe(host));
    }

    public override Task<Session> UpdateSession(UpdateSessionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_sessions.TryGetValue(request.Id, out var host))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"no session {request.Id}"));
        }

        if (request.Model.Length > 0)
        {
            host.Session.Model = request.Model;
        }

        return Task.FromResult(Describe(host));
    }

    public override Task<SendMessageResponse> SendMessage(SendMessageRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (!_sessions.TryGetValue(request.SessionId, out var host))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"no session {request.SessionId}"));
        }

        // Admitting the prompt does not wait for the turn, and does not require
        // anyone to be listening.
        host.Turn = host.Session.Run(request.Text, context.CancellationToken);

        return Task.FromResult(new SendMessageResponse { MessageId = Identifier.New() });
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

    private static Session Describe(SessionHost host) =>
        new()
        {
            Id = host.Session.SessionId,
            Model = host.Session.Model,
            ParentSessionId = host.ParentSessionId,
        };

    private SessionHost Host(string sessionId) =>
        _sessions.GetOrAdd(
            sessionId,
            id =>
            {
                var events = new EventBroker();
                return new SessionHost(new AgentSession(id, provider, events), events);
            });
}
