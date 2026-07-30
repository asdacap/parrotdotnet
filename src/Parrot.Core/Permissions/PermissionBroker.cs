using Parrot.Agent;
using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;
using ProtocolPermissionAction = Parrot.Protocol.PermissionAction;
using ProtocolPermissionChoice = Parrot.Protocol.PermissionChoice;
using ProtocolPermissionTarget = Parrot.Protocol.PermissionTarget;
using ProtocolPermissionTargetKind = Parrot.Protocol.PermissionTargetKind;

namespace Parrot.Permissions;

internal sealed class PermissionBroker : IDisposable
{
    private static readonly IReadOnlyList<PermissionChoice> DeclaredChoices =
    [
        new("grant", "Grant", PermissionDecision.Grant, false),
        new("reject", "Reject", PermissionDecision.Reject, false),
        new("reject with reason", "Reject with reason", PermissionDecision.Reject, true),
    ];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly EventBroker _events;
    private readonly EventRepository _repository;
    private readonly bool _interactive;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    public PermissionBroker(
        EventBroker events,
        EventRepository repository,
        bool interactive,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(repository);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The permission request timeout must be positive.");
        }

        _events = events;
        _repository = repository;
        _interactive = interactive;
        _timeout = timeout;
    }

    public async Task<PermissionReply> Request(
        AgentSession agentSession,
        string reason,
        IReadOnlyList<SandboxWriteTarget> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentSession);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(targets);

        var copiedTargets = targets.ToArray();
        if (copiedTargets.Length == 0)
        {
            throw new PermissionException("permission requests require at least one target");
        }

        foreach (var target in copiedTargets)
        {
            target.Validate();
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        if (!_interactive)
        {
            return new PermissionReply(PermissionDecision.Reject, string.Empty);
        }

        var id = Identifier.PermissionRequestId();
        var pending = new PendingRequest(agentSession, reason, copiedTargets);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.Add(id, pending);

            try
            {
                var published = new Event
                {
                    Id = Identifier.EventId(),
                    AgentSessionId = agentSession.SessionId,
                    PermissionPending = ToProtocol(id, pending),
                };
                _repository.Append(published, null, null);
                _events.Publish(published);
            }
            catch
            {
                _ = _pending.Remove(id);
                throw;
            }
        }

        try
        {
            return await pending.Reply.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (Remove(id, pending))
            {
                throw new PermissionException("permission request timed out");
            }

            return pending.RequireOutcome();
        }
        catch (OperationCanceledException)
        {
            if (Remove(id, pending))
            {
                throw;
            }

            return pending.RequireOutcome();
        }
    }

    public IReadOnlyList<PermissionPending> Pending()
    {
        lock (_gate)
        {
            return [.. _pending
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => ToDomain(item.Key, item.Value))];
        }
    }

    public void Reply(string requestId, string choiceValue, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentException.ThrowIfNullOrEmpty(choiceValue);
        ArgumentNullException.ThrowIfNull(reason);

        PendingRequest pending;
        PermissionChoice choice;
        var trimmedReason = reason.Trim();

        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var found))
            {
                throw new PermissionNotFoundException($"permission request not found: {requestId}");
            }

            pending = found;
            choice = DeclaredChoices.FirstOrDefault(candidate =>
                string.Equals(candidate.Value, choiceValue, StringComparison.Ordinal))
                ?? throw new PermissionException($"unknown permission choice: {choiceValue}");

            if (choice.RequiresReason && trimmedReason.Length == 0)
            {
                throw new PermissionException("the selected permission choice requires a reason");
            }

            if (!choice.RequiresReason && trimmedReason.Length > 0)
            {
                throw new PermissionException("the selected permission choice does not accept a reason");
            }

            if (choice.Decision == PermissionDecision.Grant)
            {
                try
                {
                    pending.AgentSession.WriteGrants.Grant(pending.Targets);
                }
                catch (InvalidOperationException failure)
                {
                    throw new PermissionException("permission request targets changed before approval", failure);
                }
            }

            _ = _pending.Remove(requestId);
            pending.Settle(new PermissionReply(choice.Decision, trimmedReason));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var pending = _pending.Values.ToArray();
            _pending.Clear();
            foreach (var item in pending)
            {
                item.Fail(new PermissionException("permission session closed"));
            }
        }
    }

    private static PermissionPending ToDomain(string id, PendingRequest request) =>
        new(id, request.AgentSession.SessionId, request.Reason, request.Targets, DeclaredChoices);

    private static PendingPermission ToProtocol(string id, PendingRequest request)
    {
        var pending = new PendingPermission
        {
            Id = id,
            AgentSessionId = request.AgentSession.SessionId,
            Reason = request.Reason,
        };
        pending.Targets.AddRange(request.Targets.Select(target => new ProtocolPermissionTarget
        {
            Kind = target.Kind == SandboxWriteTargetKind.File
                ? ProtocolPermissionTargetKind.File
                : ProtocolPermissionTargetKind.Directory,
            Scope = PermissionTargetScope.Write,
            Path = target.Path,
        }));
        pending.Choices.AddRange(DeclaredChoices.Select(choice => new ProtocolPermissionChoice
        {
            Value = choice.Value,
            Label = choice.Label,
            Action = choice.Decision == PermissionDecision.Grant
                ? ProtocolPermissionAction.Allow
                : ProtocolPermissionAction.Deny,
            RequiresReason = choice.RequiresReason,
        }));
        return pending;
    }

    private bool Remove(string id, PendingRequest expected)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(id, out var found) && ReferenceEquals(found, expected) && _pending.Remove(id);
        }
    }

    private sealed class PendingRequest(
        AgentSession agentSession,
        string reason,
        IReadOnlyList<SandboxWriteTarget> targets)
    {
        private PermissionException? _failure;
        private PermissionReply? _outcome;

        public AgentSession AgentSession { get; } = agentSession;

        public string Reason { get; } = reason;

        public IReadOnlyList<SandboxWriteTarget> Targets { get; } = Array.AsReadOnly(targets.ToArray());

        public TaskCompletionSource<PermissionReply> Reply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PermissionReply RequireOutcome()
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            return _outcome ?? throw new InvalidOperationException("The permission request has no settled outcome.");
        }

        public void Settle(PermissionReply outcome)
        {
            _outcome = outcome;
            _ = Reply.TrySetResult(outcome);
        }

        public void Fail(PermissionException failure)
        {
            _failure = failure;
            _ = Reply.TrySetException(failure);
        }
    }
}
