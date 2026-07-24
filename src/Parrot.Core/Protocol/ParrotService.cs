using System.Collections.Concurrent;
using Grpc.Core;
using Parrot.Agent;
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
internal sealed class ParrotService(ProviderRegistry registry, SessionStore store, ModeRegistry modes)
    : GeneratedParrot.ParrotBase, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Agent.UserSession> _userSessions = new(StringComparer.Ordinal);

    public override async Task<ListModelsResponse> ListModels(ListModelsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var listed = await registry.AvailableModels(context.CancellationToken).ConfigureAwait(false);
        var response = new ListModelsResponse();

        foreach (var model in listed)
        {
            response.Models.Add(new Model { Id = model.Model.Id, ProviderId = model.Model.ProviderId });
        }

        return response;
    }

    public override Task<ListModesResponse> ListModes(ListModesRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var response = new ListModesResponse();
        response.Modes.AddRange(modes.List().Select(id => new Mode { Id = id }));
        return Task.FromResult(response);
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

        Agent.UserSession created;

        try
        {
            _ = modes.Resolve(request.Mode, string.Empty);
            created = store.Open(resolved, model.ProviderId, model.Id, request.Mode);
        }
        catch (ModeRegistryException failure)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }

        _ = _userSessions.TryAdd(created.Id, created);

        return Task.FromResult(Describe(created));
    }

    public override Task<UserSession> UpdateSession(UpdateSessionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var found = Find(request.UserSessionId);

        ModeProfile? selectedMode = null;
        ILLMProvider? selectedProvider = null;
        LLMModel? selectedModel = null;

        try
        {
            if (request.Mode.Length > 0)
            {
                selectedMode = modes.Resolve(request.Mode, found.Id);
            }

            if (request.Model.Length > 0)
            {
                var (providerId, modelId) = SplitModel(request.Model);
                (selectedProvider, selectedModel) = registry.Resolve(providerId, modelId);
            }
        }
        catch (Exception failure) when (failure is ModeRegistryException or LLMProviderException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }

        found.Update(
            selectedProvider,
            selectedModel?.ProviderId,
            selectedModel?.Id,
            selectedMode);
        store.Publish(found);

        return Task.FromResult(Describe(found));
    }

    public override async Task<SendMessageResponse> SendMessage(
        SendMessageRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // Refused rather than defaulted: a default would decide silently
        // whether this prompt interrupts the work in flight or waits for it.
        if (request.Delivery is not (Delivery.Steer or Delivery.Queue))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "a prompt needs a delivery"));
        }

        var found = Find(request.UserSessionId);

        // The sender's id when they supplied one, so a re-send after a dropped
        // connection is recognisable as the same prompt rather than a second.
        var messageId = request.MessageId.Length > 0 ? request.MessageId : Identifier.MessageId();

        Admission admitted;

        try
        {
            admitted = await found.Send(request.Text, messageId, request.Delivery, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (InputConflictException conflict)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, conflict.Message));
        }

        return new SendMessageResponse
        {
            MessageId = admitted.Input.MessageId,
            InputId = admitted.Input.Id,
            Created = admitted.Created,
        };
    }

    // Returns once the turn has stopped and every tool call has settled, so a
    // client that sends again cannot race the turn it just stopped.
    public override async Task<InterruptResponse> Interrupt(InterruptRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        await Find(request.UserSessionId).Interrupt(context.CancellationToken).ConfigureAwait(false);

        return new InterruptResponse();
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
    //
    // All at once, not one after another: each session cancels its own drains
    // first thing, so starting them together makes shutdown as long as the
    // slowest session rather than as long as all of them added up.
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_userSessions.Values.Select(session => session.DisposeAsync().AsTask()))
            .ConfigureAwait(false);

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
        new() { Id = session.Id, Model = $"{session.ProviderId}/{session.Model}", Mode = session.Mode.Id };

    private Agent.UserSession Find(string userSessionId) =>
        _userSessions.TryGetValue(userSessionId, out var found)
            ? found
            : throw new RpcException(new Status(StatusCode.NotFound, $"no user session {userSessionId}"));
}
