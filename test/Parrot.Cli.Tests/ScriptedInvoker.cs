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
    private readonly Dictionary<string, ChannelStreamWriter<Event>> _activeEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Event>> _eventsAwaitingListeners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<QueueState>> _initialQueues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionUsageSnapshot> _initialUsage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingQuestion>> _pendingQuestions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingPermission>> _pendingPermissions = new(StringComparer.Ordinal);
    private readonly List<string> _sent = [];
    private readonly List<SendMessageRequest> _sentRequests = [];
    private readonly List<string> _sentTo = [];
    private readonly List<string> _listenedTo = [];
    private readonly List<CreateSessionRequest> _created = [];
    private readonly List<UpdateSessionRequest> _updated = [];
    private readonly List<ConfigureModelAliasRequest> _configuredAliases = [];
    private readonly List<ApplyProviderModelAliasDefaultsRequest> _appliedProviderModelAliasDefaults = [];
    private readonly List<ReplyQuestionRequest> _questionReplies = [];
    private readonly List<RejectQuestionRequest> _questionRejections = [];
    private readonly List<ReplyPermissionRequest> _permissionReplies = [];
    private readonly List<AttachmentUploadFrame> _uploadedAttachments = [];
    private readonly List<CompactRequest> _compactions = [];
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

    public IReadOnlyList<SendMessageRequest> SentRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _sentRequests.Select(request => request.Clone())];
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

    public IReadOnlyList<ApplyProviderModelAliasDefaultsRequest> AppliedProviderModelAliasDefaults
    {
        get
        {
            lock (_gate)
            {
                return [.. _appliedProviderModelAliasDefaults];
            }
        }
    }

    public int Interrupts { get; private set; }

    public IReadOnlyList<CompactRequest> Compactions
    {
        get
        {
            lock (_gate)
            {
                return [.. _compactions.Select(request => request.Clone())];
            }
        }
    }

    public IReadOnlyList<AttachmentUploadFrame> UploadedAttachments
    {
        get
        {
            lock (_gate)
            {
                return [.. _uploadedAttachments.Select(frame => frame.Clone())];
            }
        }
    }

    public bool ReplyQuestionNotFound { get; set; }

    public StatusCode? ReplyPermissionFailure { get; set; }

    public bool SessionLoaded { get; set; }

    public List<ModelAlias> ModelAliases { get; } = [];

    public List<ProviderModelAliasDefaults> ProviderModelAliasDefaults { get; } = [];

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

    public IReadOnlyList<RejectQuestionRequest> QuestionRejections
    {
        get
        {
            lock (_gate)
            {
                return [.. _questionRejections.Select(rejection => rejection.Clone())];
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

    public void RemovePendingQuestion(string requestId)
    {
        lock (_gate)
        {
            _ = GetPendingQuestions("session-1").RemoveAll(question =>
                string.Equals(question.Id, requestId, StringComparison.Ordinal));
        }
    }

    public void AddPendingPermission(PendingPermission permission)
    {
        lock (_gate)
        {
            GetPendingPermissions("session-1").Add(permission.Clone());
        }
    }

    public void SetInitialQueues(string userSessionId, params QueueState[] queues)
    {
        lock (_gate)
        {
            _initialQueues[userSessionId] = [.. queues.Select(queue => queue.Clone())];
        }
    }

    public void SetInitialUsage(string userSessionId, SessionUsageSnapshot usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        lock (_gate)
        {
            _initialUsage[userSessionId] = usage.Clone();
        }
    }

    public Task Publish(Event published)
    {
        lock (_gate)
        {
            if (_activeEvents.Count == 1)
            {
                return _activeEvents.Values.Single().WriteAsync(published);
            }

            if (_activeEvents.Count == 0 && _created.Count <= 1)
            {
                GetEventsAwaitingListeners("session-1").Add(published);
                return Task.CompletedTask;
            }

            throw new InvalidOperationException(
                "a user session must be specified when the invoker does not have exactly one active stream");
        }
    }

    public Task Publish(string userSessionId, Event published)
    {
        lock (_gate)
        {
            if (_activeEvents.TryGetValue(userSessionId, out var events))
            {
                return events.WriteAsync(published);
            }

            GetEventsAwaitingListeners(userSessionId).Add(published);
            return Task.CompletedTask;
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
            case ListProviderModelAliasDefaultsRequest:
                var listedDefaults = new ListProviderModelAliasDefaultsResponse();
                listedDefaults.Providers.Add(ProviderModelAliasDefaults);
                answered = listedDefaults;
                break;
            case ApplyProviderModelAliasDefaultsRequest applyDefaults:
                lock (_gate)
                {
                    _appliedProviderModelAliasDefaults.Add(applyDefaults);
                }

                var appliedDefaults = new ApplyProviderModelAliasDefaultsResponse();
                appliedDefaults.Aliases.Add(ModelAliases.Select(alias => alias.Clone()));
                answered = appliedDefaults;
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
                    _questionRejections.Add(reject.Clone());
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
            case CompactRequest compact:
                lock (_gate)
                {
                    _compactions.Add(compact.Clone());
                }

                answered = new CompactResponse();
                break;
            case SendMessageRequest send:
                lock (_gate)
                {
                    _sent.Add(send.Parts.Count == 0
                        ? send.Text
                        : string.Concat(send.Parts
                            .Where(part => part.ContentCase == MessageContentPart.ContentOneofCase.Text)
                            .Select(part => part.Text)));
                    _sentRequests.Add(send.Clone());
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
            events = new ChannelStreamWriter<Event>();
            _activeEvents[listen.UserSessionId] = events;
            if (!events.TryWrite(InitialQueueSnapshot(listen.UserSessionId)))
            {
                throw new InvalidOperationException("the scripted stream rejected its initial queue snapshot");
            }

            var usage = _initialUsage.TryGetValue(listen.UserSessionId, out var initialUsage)
                ? initialUsage.Clone()
                : new SessionUsageSnapshot();
            if (!events.TryWrite(new Event { SessionUsageSnapshot = usage }))
            {
                throw new InvalidOperationException("the scripted stream rejected its initial usage snapshot");
            }

            foreach (var awaiting in GetEventsAwaitingListeners(listen.UserSessionId))
            {
                if (!events.TryWrite(awaiting))
                {
                    throw new InvalidOperationException("the scripted stream rejected a queued event");
                }
            }

            _ = _eventsAwaitingListeners.Remove(listen.UserSessionId);
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
            () => ReleaseEvents(listen.UserSessionId, events));
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotSupportedException("the contract has no blocking unary call");

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        if (typeof(TRequest) != typeof(AttachmentUploadFrame)
            || typeof(TResponse) != typeof(AttachmentUploadResponse))
        {
            throw new NotSupportedException($"no scripted stream for {typeof(TRequest).Name}");
        }

        var writer = new AttachmentWriter(this);
        return new AsyncClientStreamingCall<TRequest, TResponse>(
            (IClientStreamWriter<TRequest>)(object)writer,
            (Task<TResponse>)(object)writer.Response,
            Task.FromResult(new Metadata()),
            static () => Status.DefaultSuccess,
            static () => [],
            writer.Complete);
    }

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

    private Event InitialQueueSnapshot(string userSessionId)
    {
        var snapshot = new QueueSnapshot
        {
            Revision = 0,
            ChunkIndex = 0,
            FinalChunk = true,
            RootAgentSessionId = userSessionId,
        };
        if (_initialQueues.TryGetValue(userSessionId, out var queues))
        {
            snapshot.Queues.Add(queues.Select(queue =>
            {
                var cloned = queue.Clone();
                if (cloned.OwnerAgentSessionId.Length == 0)
                {
                    cloned.OwnerAgentSessionId = userSessionId;
                }

                return cloned;
            }));
        }

        return new Event { QueueSnapshot = snapshot };
    }

    private void ReleaseEvents(string userSessionId, ChannelStreamWriter<Event> events)
    {
        lock (_gate)
        {
            if (_activeEvents.TryGetValue(userSessionId, out var active)
                && ReferenceEquals(active, events))
            {
                _ = _activeEvents.Remove(userSessionId);
            }
        }

        events.Complete();
    }

    private List<Event> GetEventsAwaitingListeners(string userSessionId)
    {
        if (!_eventsAwaitingListeners.TryGetValue(userSessionId, out var events))
        {
            events = [];
            _eventsAwaitingListeners.Add(userSessionId, events);
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

    private sealed class AttachmentWriter(ScriptedInvoker invoker) : IClientStreamWriter<AttachmentUploadFrame>
    {
        private readonly TaskCompletionSource<AttachmentUploadResponse> _response = new();

        public Task<AttachmentUploadResponse> Response => _response.Task;

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(AttachmentUploadFrame message)
        {
            lock (invoker._gate)
            {
                invoker._uploadedAttachments.Add(message.Clone());
            }

            return Task.CompletedTask;
        }

        public Task WriteAsync(AttachmentUploadFrame message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }

        public Task CompleteAsync()
        {
            Complete();
            return Task.CompletedTask;
        }

        public void Complete()
        {
            _ = _response.TrySetResult(new AttachmentUploadResponse
            {
                Artifact = new ArtifactReference { ArtifactId = "artifact-1" },
            });
        }
    }
}
