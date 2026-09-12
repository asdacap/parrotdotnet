using System.Threading.Channels;
using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class QuestionInteractionPresenter(GeneratedParrot.ParrotClient client)
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMilliseconds(250);
    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>();
    private readonly HashSet<string> _discovered = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private Session? _session;
    private long _generation;

    public Session Attach(string userSessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userSessionId);

        lock (_gate)
        {
            _generation++;
            _discovered.Clear();
            _session = new Session(userSessionId, _generation);
            return _session;
        }
    }

    public void Observe(Session session, PendingQuestion pending)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pending);

        lock (_gate)
        {
            if (!Equals(_session, session) || !_discovered.Add(pending.Id))
            {
                return;
            }

            if (!_requests.Writer.TryWrite(new Request(session, pending.Clone())))
            {
                _ = _discovered.Remove(pending.Id);
            }
        }
    }

    public void Retry(Session session, PendingQuestion pending)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pending);

        lock (_gate)
        {
            if (!Equals(_session, session))
            {
                return;
            }

            _ = _discovered.Remove(pending.Id);
        }

        Observe(session, pending);
    }

    public Request? Read()
    {
        while (_requests.Reader.TryRead(out var candidate))
        {
            lock (_gate)
            {
                if (Equals(_session, candidate.Session))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public ValueTask<bool> WaitToRead(CancellationToken cancellationToken) =>
        _requests.Reader.WaitToReadAsync(cancellationToken);

    public async Task Reconcile(Session session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var listed = await client.ListPendingQuestionsAsync(
                        new ListPendingQuestionsRequest { UserSessionId = session.UserSessionId },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    foreach (var pending in listed.Questions)
                    {
                        Observe(session, pending);
                    }
                }
                catch (RpcException) when (!cancellationToken.IsCancellationRequested)
                {
                }

                await Task.Delay(ReconciliationInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (RpcException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal sealed record Session(string UserSessionId, long Generation);

    internal sealed record Request(Session Session, PendingQuestion Pending);
}
