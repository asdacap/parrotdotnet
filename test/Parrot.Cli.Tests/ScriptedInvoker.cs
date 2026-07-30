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
    private readonly ChannelStreamWriter<Event> _events = new();
    private readonly List<string> _sent = [];
    private readonly List<string> _sentTo = [];
    private readonly List<string> _listenedTo = [];
    private readonly List<CreateSessionRequest> _created = [];
    private readonly List<UpdateSessionRequest> _updated = [];
    private readonly List<ConfigureModelAliasRequest> _configuredAliases = [];
    private readonly List<ReplyQuestionRequest> _questionReplies = [];
    private readonly Lock _gate = new();

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

    public IReadOnlyList<ReplyQuestionRequest> QuestionReplies
    {
        get
        {
            lock (_gate)
            {
                return [.. _questionReplies];
            }
        }
    }

    public int Interrupts { get; private set; }

    public bool ReplyQuestionNotFound { get; set; }

    public bool SessionLoaded { get; set; }

    public List<ModelAlias> ModelAliases { get; } = [];

    public List<PendingQuestion> PendingQuestions { get; } = [];

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

    public Task Publish(Event published) => _events.WriteAsync(published);

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        object answered;
        RpcException? failure = null;

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
            case ListPendingQuestionsRequest:
                var listedQuestions = new ListPendingQuestionsResponse();
                lock (_gate)
                {
                    listedQuestions.Questions.Add(PendingQuestions.Select(question => question.Clone()));
                }

                answered = listedQuestions;
                break;
            case ReplyQuestionRequest reply:
                lock (_gate)
                {
                    _questionReplies.Add(reply.Clone());
                    _ = PendingQuestions.RemoveAll(question => string.Equals(question.Id, reply.QuestionRequestId, StringComparison.Ordinal));
                }

                answered = new ReplyQuestionResponse();
                if (ReplyQuestionNotFound)
                {
                    failure = new RpcException(new Status(StatusCode.NotFound, "question is no longer pending"));
                }

                break;
            case RejectQuestionRequest reject:
                lock (_gate)
                {
                    _ = PendingQuestions.RemoveAll(question => string.Equals(question.Id, reject.QuestionRequestId, StringComparison.Ordinal));
                }

                answered = new RejectQuestionResponse();
                break;
            case ListModesRequest:
                var listedModes = new ListModesResponse();
                listedModes.Modes.Add(Modes);
                answered = listedModes;
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
            failure is null
                ? Task.FromResult((TResponse)answered)
                : Task.FromException<TResponse>(failure),
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

        if (_events.Reader is not IAsyncStreamReader<TResponse> stream)
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
}
