using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class TerminalSessionTarget(
    CallInvoker invoker,
    UserSession session,
    IDisposable? owner) : IDisposable
{
    private readonly IDisposable? _owner = owner;
    private bool _disposed;

    public CallInvoker Invoker { get; } = invoker;

    public GeneratedParrot.ParrotClient Client { get; } = new(invoker);

    public UserSession Session { get; } = session;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner?.Dispose();
    }
}
