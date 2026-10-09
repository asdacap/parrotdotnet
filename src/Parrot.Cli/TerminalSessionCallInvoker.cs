using Grpc.Core;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class TerminalSessionCallInvoker : CallInvoker
{
    private CallInvoker _target;

    public TerminalSessionCallInvoker(CallInvoker initial)
    {
        _target = initial;
        Client = new GeneratedParrot.ParrotClient(this);
    }

    public CallInvoker Target
    {
        get => Volatile.Read(ref _target);
        set => Volatile.Write(ref _target, value);
    }

    public GeneratedParrot.ParrotClient Client { get; }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        Target.BlockingUnaryCall(method, host, options, request);

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        Target.AsyncUnaryCall(method, host, options, request);

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        Target.AsyncServerStreamingCall(method, host, options, request);

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        Target.AsyncClientStreamingCall(method, host, options);

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        Target.AsyncDuplexStreamingCall(method, host, options);
}
