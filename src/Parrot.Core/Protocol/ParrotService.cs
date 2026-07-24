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
internal sealed class ParrotService(ProviderRegistry registry, SessionStore store)
    : GeneratedParrot.ParrotBase, IDisposable
{
    private readonly ConcurrentDictionary<string, Agent.UserSession> _userSessions = new(StringComparer.Ordinal);

    // Which provider each session is bound to, so a /model that crosses
    // providers is refused rather than silently answered by the wrong one.
    private readonly ConcurrentDictionary<string, string> _sessionProviders = new(StringComparer.Ordinal);

    public override async Task<ListModelsResponse> ListModels(ListModelsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await registry.RefreshAll(context.CancellationToken).ConfigureAwait(false);
        var response = new ListModelsResponse();

        foreach (var model in registry.AllModels())
        {
            response.Models.Add(new Model { Id = model.Id, ProviderId = model.ProviderId });
        }

        return response;
    }

    public override Task<UserSession> CreateSession(CreateSessionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (providerId, modelId) = SplitModel(request.Model);
        ILLMProvider resolved;
        LLMModel model;

        try
        {
            (resolved, model) = registry.Resolve(providerId, modelId);
        }
        catch (LLMProviderException failure)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }

        // The session carries the bare model id; the composition already holds
        // the provider that this selection resolved to.
        var created = store.Open(model.Id);
        _ = _userSessions.TryAdd(created.Id, created);
        _ = _sessionProviders.TryAdd(created.Id, resolved.Id);

        return Task.FromResult(Describe(created));
    }

    public override Task<UserSession> UpdateSession(UpdateSessionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var found = Find(request.UserSessionId);

        if (request.Model.Length > 0)
        {
            var (providerId, modelId) = SplitModel(request.Model);
            var bound = _sessionProviders.GetValueOrDefault(request.UserSessionId, string.Empty);

            if (providerId.Length > 0 && bound.Length > 0 && providerId != bound)
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"this session is bound to {bound}; use /clear to start one on {providerId}"));
            }

            found.Model = modelId;
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

    // Selection is "provider/model"; the model portion keeps any vendor prefix,
    // so the split is on the first slash only.
    private static (string ProviderId, string ModelId) SplitModel(string selection)
    {
        var slash = selection.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (string.Empty, selection) : (selection[..slash], selection[(slash + 1)..]);
    }

    private static UserSession Describe(Agent.UserSession session) =>
        new() { Id = session.Id, Model = session.Model };

    private Agent.UserSession Find(string userSessionId) =>
        _userSessions.TryGetValue(userSessionId, out var found)
            ? found
            : throw new RpcException(new Status(StatusCode.NotFound, $"no user session {userSessionId}"));
}
