using Grpc.Core;

namespace Parrot.Protocol;

// Local mode binds no socket (principle 12): the generated client reaches the
// service through this invoker instead of Kestrel. Only the server-streaming
// shape the contract uses is implemented; the rest throw rather than pretend.
internal sealed class InProcessCallInvoker(ParrotService service) : CallInvoker
{
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        string? host,
        CallOptions options,
        TRequest request)
    {
        var writer = new ChannelStreamWriter<TResponse>();
        var context = new InProcessServerCallContext(options.CancellationToken);

        var call = Dispatch(request, writer, context);

        return new AsyncServerStreamingCall<TResponse>(
            writer.Reader,
            Task.FromResult(new Metadata()),
            () => call.IsFaulted ? Status.DefaultCancelled : Status.DefaultSuccess,
            static () => [],
            writer.Complete);
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotImplementedException("the contract has no blocking unary call");

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotImplementedException("the contract has no unary call");

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotImplementedException("the contract has no client-streaming call");

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotImplementedException("the contract has no duplex call");

    private static async Task Complete<TResponse>(Task call, ChannelStreamWriter<TResponse> writer)
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

    private Task Dispatch<TRequest, TResponse>(
        TRequest request,
        ChannelStreamWriter<TResponse> writer,
        InProcessServerCallContext context)
        where TRequest : class
        where TResponse : class
    {
        if (request is ChatRequest chat && writer is ChannelStreamWriter<Event> events)
        {
            return Complete(service.Chat(chat, events, context), events);
        }

        throw new NotImplementedException($"no in-process route for {typeof(TRequest).Name}");
    }
}
