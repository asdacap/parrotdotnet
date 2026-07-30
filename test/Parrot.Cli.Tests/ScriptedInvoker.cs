using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

// A server that never finishes the turn. It records what the driver sent and
// publishes whatever the test wants published, so the driver can be observed
// mid-turn -- which is the only state where a queue is visible.
internal sealed class ScriptedInvoker : CallInvoker
{
    // The production stream, not a second implementation of it: both halves of
    // an in-process stream are what ChannelStreamWriter already is.
    private readonly Dictionary<string, ChannelStreamWriter<Event>> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingQuestion>> _pendingQuestions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingPermission>> _pendingPermissions = new(StringComparer.Ordinal);
    private readonly List<string> _sent = [];
    private readonly List<string> _sentTo = [];
    private readonly List<string> _listenedTo = [];
    private readonly List<CreateSessionRequest> _created = [];
    private readonly List<UpdateSessionRequest> _updated = [];
    private readonly List<ConfigureModelAliasRequest> _configuredAliases = [];
    private readonly List<ReplyQuestionRequest> _questionReplies = [];
    private readonly List<ReplyPermissionRequest> _permissionReplies = [];
    private readonly Lock _gate = new();
    private int _pendingQuestionLists;
    private int _pendingPermissionLists;

    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    public IReadOnlyList<string> SentTo
    {
        get
        {
            lock (_gate)
            {
                return [.. _sentTo];
            }
        }
    }

    public IReadOnlyList<string> ListenedTo
    {
        get
        {
            lock (_gate)
            {
                return [.. _listenedTo];
            }
        }
    }

    public IReadOnlyList<CreateSessionRequest> Created
    {
        get
        {
            lock (_gate)
            {
                return [.. _created];
            }
        }
    }

    public IReadOnlyList<UpdateSessionRequest> Updated
    {
        get
        {
            lock (_gate)
            {
                return [.. _updated];
            }
        }
    }

    public List<PendingQuestion> PendingQuestions => GetPendingQuestions("session-1");

    public IReadOnlyList<ConfigureModelAliasRequest> ConfiguredAliases
    {
        get
        {
            lock (_gate)
            {
                return [.. _configuredAliases];
            }
        }
    }

    public int Interrupts { get; private set; }

    public bool ReplyQuestionNotFound { get; set; }

    public StatusCode? ReplyPermissionFailure { get; set; }

    public bool SessionLoaded { get; set; }

    public List<ModelAlias> ModelAliases { get; } = [];

    public int PendingQuestionLists
    {
        get
        {
            lock (_gate)
            {
                return _pendingQuestionLists;
            }
        }
    }

    public IReadOnlyList<ReplyQuestionRequest> QuestionReplies
    {
        get
        {
            lock (_gate)
            {
                return [.. _questionReplies.Select(reply => reply.Clone())];
            }
        }
    }

    public IReadOnlyList<ReplyPermissionRequest> PermissionReplies
    {
        get
        {
            lock (_gate)
            {
                return [.. _permissionReplies.Select(reply => reply.Clone())];
            }
        }
    }

    public int PendingPermissionLists
    {
        get
        {
            lock (_gate)
            {
                return _pendingPermissionLists;
            }
        }
    }

    public List<SessionSummary> Sessions { get; } = [];

    public bool SessionListingUnavailable { get; set; }

    public List<Model> Models { get; } =
    [
        new() { ProviderId = "provider", Id = "model" },
        new() { ProviderId = "provider", Id = "other" },
    ];

    public List<Mode> Modes { get; } =
    [
        new() { Id = "build" },
        new() { Id = "plan" },
        new() { Id = "query" },
    ];

    public void AddModel(Model model)
    {
        lock (_gate)
        {
            Models.Add(model);
        }
    }

    public void AddPendingQuestion(PendingQuestion question)
    {
        lock (_gate)
        {
            GetPendingQuestions("session-1").Add(question.Clone());
        }
    }

    public void AddPendingPermission(PendingPermission permission)
    {
        lock (_gate)
        {
            GetPendingPermissions("session-1").Add(permission.Clone());
        }
    }

    public Task Publish(Event published)
    {
        lock (_gate)
        {
            var sessionIds = _events.Keys.ToArray();
            if (sessionIds.Length == 1)
            {
                return _events[sessionIds[0]].WriteAsync(published);
            }

            if (sessionIds.Length == 0 && _created.Count <= 1)
            {
                return GetEvents("session-1").WriteAsync(published);
            }

            throw new InvalidOperationException(
                "a user session must be specified when the invoker does not have exactly one stream");
        }
    }

    public Task Publish(string userSessionId, Event published)
    {
        lock (_gate)
        {
            return GetEvents(userSessionId).WriteAsync(published);
        }
    }

    public void AddPendingQuestion(string userSessionId, PendingQuestion question)
    {
        lock (_gate)
        {
            GetPendingQuestions(userSessionId).Add(question);
        }
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        object answered;

        switch (request)
        {
            case CreateSessionRequest create:
                lock (_gate)
                {
                    _created.Add(create);
                }

                answered = new UserSession
                {
                    Id = $"session-{_created.Count}",
                    Model = create.Model,
                    Mode = create.Mode,
                    Loaded = SessionLoaded,
                };
                break;
            case UpdateSessionRequest update:
                lock (_gate)
                {
                    _updated.Add(update);
                }

                answered = new UserSession
                {
                    Id = update.UserSessionId,
                    Model = update.Model,
                    Mode = update.Mode,
                };
                break;
            case ListModelsRequest:
                var listedModels = new ListModelsResponse();
                listedModels.Models.Add(Models);
                answered = listedModels;
                break;
            case ListModelAliasesRequest:
                var listedAliases = new ListModelAliasesResponse();
                listedAliases.Aliases.Add(ModelAliases);
                answered = listedAliases;
                break;
            case ConfigureModelAliasRequest configure:
                lock (_gate)
                {
                    _configuredAliases.Add(configure);
                }

                var configured = ModelAliases.Single(alias =>
                    string.Equals(alias.Name, configure.Name, StringComparison.Ordinal));
                configured.ModelString = configure.ModelString;
                answered = new ConfigureModelAliasResponse { Alias = configured.Clone() };
                break;
            case ListPendingQuestionsRequest listQuestions:
                var listedQuestions = new ListPendingQuestionsResponse();
                lock (_gate)
                {
                    _pendingQuestionLists++;
                    listedQuestions.Questions.Add(GetPendingQuestions(listQuestions.UserSessionId).Select(question => question.Clone()));
                }

                answered = listedQuestions;
                break;
            case ListPendingPermissionsRequest listPermissions:
                var listedPermissions = new ListPendingPermissionsResponse();
                lock (_gate)
                {
                    _pendingPermissionLists++;
                    listedPermissions.Permissions.Add(
                        GetPendingPermissions(listPermissions.UserSessionId).Select(permission => permission.Clone()));
                }

                answered = listedPermissions;
                break;
            case ReplyPermissionRequest permissionReply:
                lock (_gate)
                {
                    _permissionReplies.Add(permissionReply.Clone());
                    _ = GetPendingPermissions(permissionReply.UserSessionId).RemoveAll(permission =>
                        string.Equals(permission.Id, permissionReply.PermissionRequestId, StringComparison.Ordinal));
                }

                answered = new ReplyPermissionResponse();
                if (ReplyPermissionFailure is { } permissionFailure)
                {
                    return Failed<TResponse>(permissionFailure, "scripted permission failure");
                }

                break;
            case ReplyQuestionRequest reply:
                lock (_gate)
                {
                    _questionReplies.Add(reply.Clone());
                    _ = GetPendingQuestions(reply.UserSessionId).RemoveAll(question => string.Equals(question.Id, reply.QuestionRequestId, StringComparison.Ordinal));
                }

                answered = new ReplyQuestionResponse();
                if (ReplyQuestionNotFound)
                {
                    return Failed<TResponse>(StatusCode.NotFound, "question is no longer pending");
                }

                break;
            case RejectQuestionRequest reject:
                lock (_gate)
                {
                    _ = GetPendingQuestions(reject.UserSessionId).RemoveAll(question => string.Equals(question.Id, reject.QuestionRequestId, StringComparison.Ordinal));
                }

                answered = new RejectQuestionResponse();
                break;
            case ListModesRequest:
                var listedModes = new ListModesResponse();
                listedModes.Modes.Add(Modes);
                answered = listedModes;
                break;
            case ListSessionsRequest:
                if (SessionListingUnavailable)
                {
                    return Failed<TResponse>(StatusCode.Unimplemented, "method is not implemented");
                }

                var listedSessions = new ListSessionsResponse();
                listedSessions.Sessions.Add(Sessions);
                answered = listedSessions;
                break;
            case SendMessageRequest send:
                lock (_gate)
                {
                    _sent.Add(send.Text);
                    _sentTo.Add(send.UserSessionId);
                }

                answered = new SendMessageResponse
                {
                    MessageId = $"msg-{_sent.Count}",
                    InputId = $"inp-{_sent.Count}",
                    Created = true,
                };
                break;

            case InterruptRequest:
                Interrupts++;
                answered = new InterruptResponse();
                break;

            default:
                throw new NotSupportedException($"no scripted answer for {typeof(TRequest).Name}");
        }

        return new AsyncUnaryCall<TResponse>(
            Task.FromResult((TResponse)answered),
            Task.FromResult(new Metadata()),
            static () => Status.DefaultSuccess,
            static () => [],
            static () => { });
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        if (request is not ListenRequest listen)
        {
            throw new NotSupportedException($"no scripted stream for {typeof(TRequest).Name}");
        }

        lock (_gate)
        {
            _listenedTo.Add(listen.UserSessionId);
        }

        ChannelStreamWriter<Event> events;
        lock (_gate)
        {
            events = GetEvents(listen.UserSessionId);
        }

        if (events.Reader is not IAsyncStreamReader<TResponse> stream)
        {
            throw new NotSupportedException($"no scripted stream for {typeof(TResponse).Name}");
        }

        return new AsyncServerStreamingCall<TResponse>(
            stream,
            Task.FromResult(new Metadata()),
            static () => Status.DefaultSuccess,
            static () => [],
            static () => { });
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotSupportedException("the contract has no blocking unary call");

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotSupportedException("the contract has no client-streaming call");

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotSupportedException("the contract has no duplex call");

    private static AsyncUnaryCall<TResponse> Failed<TResponse>(StatusCode code, string detail) =>
        new(
            Task.FromException<TResponse>(new RpcException(new Status(code, detail))),
            Task.FromResult(new Metadata()),
            static () => Status.DefaultSuccess,
            static () => [],
            static () => { });

    private ChannelStreamWriter<Event> GetEvents(string userSessionId)
    {
        if (!_events.TryGetValue(userSessionId, out var events))
        {
            events = new ChannelStreamWriter<Event>();
            _events.Add(userSessionId, events);
        }

        return events;
    }

    private List<PendingQuestion> GetPendingQuestions(string userSessionId)
    {
        if (!_pendingQuestions.TryGetValue(userSessionId, out var questions))
        {
            questions = [];
            _pendingQuestions.Add(userSessionId, questions);
        }

        return questions;
    }

    private List<PendingPermission> GetPendingPermissions(string userSessionId)
    {
        if (!_pendingPermissions.TryGetValue(userSessionId, out var permissions))
        {
            permissions = [];
            _pendingPermissions.Add(userSessionId, permissions);
        }

        return permissions;
    }
}
