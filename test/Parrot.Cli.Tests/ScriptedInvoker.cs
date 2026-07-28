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
    private readonly List<CreateSessionRequest> _created = [];
    private readonly List<UpdateSessionRequest> _updated = [];
    private readonly List<Model> _models = [];
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

    public int Interrupts { get; private set; }

    public void AddModel(Model model)
    {
        lock (_gate)
        {
            _models.Add(model);
        }
    }

    public Task Publish(Event published) => _events.WriteAsync(published);

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
                lock (_gate)
                {
                    answered = new ListModelsResponse { Models = { _models } };
                }

                break;
            case ListModesRequest:
                answered = new ListModesResponse
                {
                    Modes = { new Mode { Id = "build" }, new Mode { Id = "plan" }, new Mode { Id = "query" } },
                };
                break;
            case SendMessageRequest send:
                lock (_gate)
                {
                    _sent.Add(send.Text);
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
