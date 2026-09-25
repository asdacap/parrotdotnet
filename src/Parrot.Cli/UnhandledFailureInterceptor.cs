using Grpc.Core;
using Grpc.Core.Interceptors;
using Parrot.Diagnostics;

namespace Parrot.Cli;

// A handler failure that is not an RpcException would otherwise reach the
// client as "Exception was thrown by handler" and leave no trace in the log.
internal sealed class UnhandledFailureInterceptor(IDiagnosticLog diagnostics) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        try
        {
            return await continuation(request, context).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsUnhandled(failure))
        {
            throw Translate(failure, context);
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        try
        {
            return await continuation(requestStream, context).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsUnhandled(failure))
        {
            throw Translate(failure, context);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsUnhandled(failure))
        {
            throw Translate(failure, context);
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsUnhandled(failure))
        {
            throw Translate(failure, context);
        }
    }

    private static bool IsUnhandled(Exception failure) =>
        failure is not RpcException and not OperationCanceledException;

    private RpcException Translate(Exception failure, ServerCallContext context)
    {
        diagnostics.Write(new DiagnosticEvent("transport", "handler.failure", DiagnosticSeverity.Error)
        {
            RequestId = context.Method,
            ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            Outcome = "failure",
        });
        return new RpcException(new Status(StatusCode.Internal, failure.Message, failure));
    }
}
