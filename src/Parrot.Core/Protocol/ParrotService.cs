using System.Collections.Concurrent;
using Grpc.Core;
using Parrot.Llm;
using Parrot.Store;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Protocol;

// The gRPC contract. Commands in, one flat event stream out.
//
// It deals in user sessions only. Agent sessions live inside one and are not
// addressable here, because a user never spawns a subagent -- an agent does.
//
// The base class is generated: `service Parrot` in parrot.proto produces a
// container class `Parrot.Protocol.Parrot` holding `ParrotBase` for servers and
// `ParrotClient` for clients. The alias exists because `Parrot` is also this
// repository's root namespace, and `Parrot.ParrotBase` reads as though it were
// a namespace lookup.
internal sealed class ParrotService(ILLMProvider provider, SessionStore store)
    : GeneratedParrot.ParrotBase, IDisposable
{
    private readonly ConcurrentDictionary<string, Agent.UserSession> _userSessions = new(StringComparer.Ordinal);

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

    public override Task<UserSession> CreateSession(CreateSessionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var created = store.Open(request.Model, provider);
        _ = _userSessions.TryAdd(created.Id, created);

        return Task.FromResult(Describe(created));
    }

    public override Task<UserSession> UpdateSession(UpdateSessionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var found = Find(request.UserSessionId);

        if (request.Model.Length > 0)
        {
            found.Model = request.Model;
        }

        return Task.FromResult(Describe(found));
    }

    public override Task<SendMessageResponse> SendMessage(SendMessageRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // Admitting the prompt does not wait for the turn, and does not require
        // anyone to be listening.
        Find(request.UserSessionId).Send(request.Text, context.CancellationToken);

        return Task.FromResult(new SendMessageResponse { MessageId = Identifier.EventId() });
    }

    // Indefinite. It ends when the client stops listening or the call is
    // cancelled -- not when a turn finishes, because a subagent spawned by the
    // agent keeps publishing to this same stream long afterwards.
    public override async Task Listen(
        ListenRequest request,
        IServerStreamWriter<Event> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var found = Find(request.UserSessionId);

        await foreach (var published in found.Listen(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(published, context.CancellationToken).ConfigureAwait(false);
        }
    }

    // M1 keeps every user session for the process lifetime, which is right for
    // a one-shot CLI. Evicting an idle session is an M6 concern, when a server
    // outlives the sessions it hosts.
    public void Dispose()
    {
        foreach (var session in _userSessions.Values)
        {
            session.Dispose();
        }

        _userSessions.Clear();
    }

    private static UserSession Describe(Agent.UserSession session) =>
        new() { Id = session.Id, Model = session.Model };

    private Agent.UserSession Find(string userSessionId) =>
        _userSessions.TryGetValue(userSessionId, out var found)
            ? found
            : throw new RpcException(new Status(StatusCode.NotFound, $"no user session {userSessionId}"));
}
