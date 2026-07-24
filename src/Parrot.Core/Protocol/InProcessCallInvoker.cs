using Grpc.Core;

namespace Parrot.Protocol;

// Local mode binds no socket (principle 12): the generated client reaches the
// service through this invoker instead of Kestrel. Only the two call shapes the
// contract uses are implemented; the rest throw rather than pretend.
internal sealed class InProcessCallInvoker(ParrotService service) : CallInvoker
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        string? host,
        CallOptions options,
        TRequest request)
    {
        var context = new InProcessServerCallContext(options.CancellationToken);

        var response = request switch
        {
            ListModelsRequest list => Cast<TResponse, ListModelsResponse>(service.ListModels(list, context)),
            CreateSessionRequest create => Cast<TResponse, Session>(service.CreateSession(create, context)),
            UpdateSessionRequest update => Cast<TResponse, Session>(service.UpdateSession(update, context)),
            SendMessageRequest send => Cast<TResponse, SendMessageResponse>(service.SendMessage(send, context)),
            _ => throw new NotImplementedException($"no in-process route for {typeof(TRequest).Name}"),
        };

        return new AsyncUnaryCall<TResponse>(
            response,
            Task.FromResult(new Metadata()),
            static () => Status.DefaultSuccess,
            static () => [],
            static () => { });
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        string? host,
        CallOptions options,
        TRequest request)
    {
        var writer = new ChannelStreamWriter<TResponse>();
        var context = new InProcessServerCallContext(options.CancellationToken);

        if (request is not ListenRequest listen || writer is not ChannelStreamWriter<Event> events)
        {
            throw new NotImplementedException($"no in-process route for {typeof(TRequest).Name}");
        }

        Drain(service.Listen(listen, events, context), writer);

        return new AsyncServerStreamingCall<TResponse>(
            writer.Reader,
            Task.FromResult(new Metadata()),
            static () => Status.DefaultSuccess,
            static () => [],
            writer.Complete);
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotImplementedException("the contract has no blocking unary call");

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotImplementedException("the contract has no client-streaming call");

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotImplementedException("the contract has no duplex call");

    private static async Task<TResponse> Cast<TResponse, TActual>(Task<TActual> response)
        where TResponse : class
        where TActual : class =>
        await response.ConfigureAwait(false) as TResponse
            ?? throw new InvalidOperationException($"a {typeof(TActual).Name} cannot answer a {typeof(TResponse).Name}");

    private static async void Drain<TResponse>(Task call, ChannelStreamWriter<TResponse> writer)
        where TResponse : class
    {
        try
        {
            await call.ConfigureAwait(false);
            writer.Complete();
        }
        catch (Exception failure)
        {
            // Deliberate containment: the failure travels to the client as a
            // faulted stream rather than as an unobserved task exception.
            writer.Fault(failure);
        }
    }
}
