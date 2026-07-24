using Grpc.Core;

namespace Parrot.Protocol;

// Everything a local call genuinely has, and nothing invented. There is no
// peer, no deadline, and no auth context in process.
internal sealed class InProcessServerCallContext(CancellationToken cancellationToken) : ServerCallContext
{
    protected override string MethodCore => "in-process";

    protected override string HostCore => "in-process";

    protected override string PeerCore => "in-process";

    protected override DateTime DeadlineCore => DateTime.MaxValue;

    protected override Metadata RequestHeadersCore { get; } = [];

    protected override CancellationToken CancellationTokenCore { get; } = cancellationToken;

    protected override Metadata ResponseTrailersCore { get; } = [];

    protected override Status StatusCore { get; set; }

    protected override WriteOptions? WriteOptionsCore { get; set; }

    protected override AuthContext AuthContextCore { get; } = new(string.Empty, new Dictionary<string, List<AuthProperty>>(StringComparer.Ordinal));

    protected override IDictionary<object, object> UserStateCore { get; } = new Dictionary<object, object>();

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotSupportedException("an in-process call has nothing to propagate to");

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
