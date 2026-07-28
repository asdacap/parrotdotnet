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

        return new AsyncUnaryCall<TResponse>(
            Unary<TResponse>(request, context),
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

        _ = Drain(listen, events, context);

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

    // The service call is started here rather than handed in, so the task
    // being awaited is one this method owns.
    private async Task<TResponse> Unary<TResponse>(object request, InProcessServerCallContext context)
        where TResponse : class
    {
        object answered = request switch
        {
            ListModelsRequest list => await service.ListModels(list, context).ConfigureAwait(false),
            ListModelAliasesRequest list => await service.ListModelAliases(list, context).ConfigureAwait(false),
            ConfigureModelAliasRequest configure =>
                await service.ConfigureModelAlias(configure, context).ConfigureAwait(false),
            ListModesRequest list => await service.ListModes(list, context).ConfigureAwait(false),
            CreateSessionRequest create => await service.CreateSession(create, context).ConfigureAwait(false),
            UpdateSessionRequest update => await service.UpdateSession(update, context).ConfigureAwait(false),
            SendMessageRequest send => await service.SendMessage(send, context).ConfigureAwait(false),
            InterruptRequest interrupt => await service.Interrupt(interrupt, context).ConfigureAwait(false),
            _ => throw new NotImplementedException($"no in-process route for {request.GetType().Name}"),
        };

        return answered as TResponse
            ?? throw new InvalidOperationException($"a {answered.GetType().Name} cannot answer a {typeof(TResponse).Name}");
    }

    // Task, not void: an async void that throws takes the process down. It
    // catches everything, so discarding the task at the call site loses
    // nothing.
    private async Task Drain(
        ListenRequest request,
        ChannelStreamWriter<Event> writer,
        InProcessServerCallContext context)
    {
        try
        {
            await service.Listen(request, writer, context).ConfigureAwait(false);
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
