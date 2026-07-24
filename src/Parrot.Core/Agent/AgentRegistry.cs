using System.Diagnostics;
using System.Text;
using Parrot.Events;
using Parrot.Protocol;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Agent;

// Owns every child agent in one user session. Entries are retained after they
// finish so waiting is repeatable, while only running entries consume the
// per-parent concurrency limit. _gate protects admission, names, and active counts.
internal sealed class AgentRegistry(
    IAgentSessionFactory agentSessions,
    EventBroker eventBroker,
    EventRepository eventRepository,
    CancellationToken lifetime) : IAsyncDisposable, IActiveWorkSource
{
    private const int MaxDepth = 4;
    private const int MaxConcurrentPerParent = 4;
    private const int MaxRetained = 1024;
    private const int MaxPromptBytes = 1024 * 1024;
    private const int MaxResultBytes = 1024 * 1024;

    private readonly Dictionary<string, AgentEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _activeByParent = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private readonly Lock _gate = new();

    private bool _accepting = true;
    private Task? _shutdown;

    public AgentTaskResult Spawn(AgentSession parent, string prompt, string requestedName)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new AgentRegistryException("no prompt given");
        }

        if (Encoding.UTF8.GetByteCount(prompt) > MaxPromptBytes)
        {
            throw new AgentRegistryException("subagent prompt exceeds 1048576 bytes");
        }

        lock (_gate)
        {
            if (!_accepting)
            {
                throw new AgentRegistryException("the user session is shutting down");
            }

            if (_entries.Count >= MaxRetained)
            {
                throw new AgentRegistryException("subagent retention limit reached");
            }

            var depth = parent.Depth + 1;

            if (depth > MaxDepth)
            {
                throw new AgentRegistryException("subagent depth limit reached");
            }

            _ = _activeByParent.TryGetValue(parent.SessionId, out var parentActive);

            if (parentActive >= MaxConcurrentPerParent)
            {
                throw new AgentRegistryException("subagent concurrency limit reached for this parent");
            }

            var sessionId = Identifier.AgentSession();
            var name = UniqueName(requestedName, sessionId);
            var identity = AgentIdentity.Child(sessionId, parent.SessionId, name, depth);
            var selection = parent.Selection();
            var child = agentSessions.Create(
                identity,
                selection.Provider,
                selection.Model,
                eventBroker,
                eventRepository,
                mode: null,
                status: null,
                _lifetime.Token);
            var entry = new AgentEntry(child, parent.SessionId, name);

            _entries.Add(sessionId, entry);
            _names.Add(name, sessionId);
            _activeByParent[parent.SessionId] = parentActive + 1;
            entry.Start(Execute(entry, child, prompt, followUp: false));

            return entry.Result(
                AgentTaskStatus.Running,
                yielded: false,
                elapsedMilliseconds: 0,
                string.Empty,
                string.Empty);
        }
    }

    public async Task<AgentHandle> Get(
        AgentSession requester, string sessionIdOrName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requester);
        AgentEntry entry;

        lock (_gate)
        {
            entry = Resolve(requester, sessionIdOrName);
        }

        await entry.Operation.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_accepting)
            {
                _ = entry.Operation.Release();
                throw new AgentRegistryException("the user session is shutting down");
            }

            return new AgentHandle(entry.Child, entry.Running, entry.Operation);
        }
    }

    public void FollowUp(AgentHandle child)
    {
        ArgumentNullException.ThrowIfNull(child);

        lock (_gate)
        {
            if (!_accepting)
            {
                throw new AgentRegistryException("the user session is shutting down");
            }

            if (!_entries.TryGetValue(child.Session.SessionId, out var entry)
                || !ReferenceEquals(entry.Child, child.Session))
            {
                throw new AgentRegistryException($"child agent not found: {child.Session.SessionId}");
            }

            _ = _activeByParent.TryGetValue(entry.ParentSessionId, out var parentActive);

            if (parentActive >= MaxConcurrentPerParent)
            {
                throw new AgentRegistryException("subagent concurrency limit reached for this parent");
            }

            _activeByParent[entry.ParentSessionId] = parentActive + 1;
            entry.Start(Execute(entry, entry.Child, string.Empty, followUp: true));
        }
    }

    public async Task<AgentTaskResult> Wait(
        AgentSession requester,
        string sessionIdOrName,
        int yieldAfterMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requester);

        AgentEntry entry;
        Task<AgentExecution> completion;

        lock (_gate)
        {
            entry = Resolve(requester, sessionIdOrName);
            completion = entry.Completion;
        }

        var started = Stopwatch.GetTimestamp();

        if (yieldAfterMilliseconds == 0)
        {
            var completed = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Terminal(entry, completed, Elapsed(started));
        }

        using var yielded = new CancellationTokenSource(TimeSpan.FromMilliseconds(yieldAfterMilliseconds));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, yielded.Token);

        try
        {
            var completed = await completion.WaitAsync(wait.Token).ConfigureAwait(false);
            return Terminal(entry, completed, Elapsed(started));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return entry.Result(
                AgentTaskStatus.Running,
                yielded: true,
                Elapsed(started),
                string.Empty,
                string.Empty);
        }
    }

    public IReadOnlyList<ActiveWorkObservation> Active()
    {
        lock (_gate)
        {
            return [.. _entries.Values
                .Where(entry => !entry.Completion.IsCompleted)
                .Select(entry => new ActiveWorkObservation(
                    entry.SessionId,
                    entry.Name,
                    ActiveWorkKind.Agent,
                    ActiveWorkState.Running))
                .OrderBy(item => item.Id, StringComparer.Ordinal)];
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is null)
            {
                _accepting = false;
                _shutdown = Shutdown([.. _entries.Values.Select(entry => entry.Execution)]);
            }

            return new ValueTask(_shutdown);
        }
    }

    private static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static AgentTaskResult Terminal(AgentEntry entry, AgentExecution completed, long elapsedMilliseconds) =>
        completed.Status switch
        {
            AgentExecutionStatus.Succeeded =>
                entry.Result(
                    AgentTaskStatus.Succeeded,
                    yielded: false,
                    elapsedMilliseconds,
                    completed.Output,
                    completed.Error),
            AgentExecutionStatus.Failed =>
                entry.Result(
                    AgentTaskStatus.Failed,
                    yielded: false,
                    elapsedMilliseconds,
                    completed.Output,
                    completed.Error),
            _ => entry.Result(
                AgentTaskStatus.Canceled,
                yielded: false,
                elapsedMilliseconds,
                completed.Output,
                completed.Error),
        };

    private static AgentExecution Bounded(AgentExecution execution) =>
        execution with
        {
            Output = Bounded(execution.Output),
            Error = Bounded(execution.Error),
        };

    private static string Bounded(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaxResultBytes)
        {
            return value;
        }

        var characters = 0;
        var bytes = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxResultBytes)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }

        return value[..characters];
    }

    private static string Sanitize(string name)
    {
        var sanitized = new StringBuilder(name.Length);
        var separator = false;

        foreach (var character in name)
        {
            if (character is >= 'A' and <= 'Z')
            {
                _ = sanitized.Append(char.ToLowerInvariant(character));
                separator = false;
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                _ = sanitized.Append(character);
                separator = false;
            }
            else if (sanitized.Length > 0)
            {
                separator = true;
            }

            if (separator && sanitized.Length > 0 && sanitized[^1] != '-')
            {
                _ = sanitized.Append('-');
            }
        }

        return sanitized.ToString().TrimEnd('-');
    }

    private async Task Shutdown(Task[] running)
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(running).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task Execute(AgentEntry entry, AgentSession child, string prompt, bool followUp)
    {
        await Task.Yield();

        AgentExecution completed;
        var started = false;

        try
        {
            await Emit(
                child.SessionId,
                new AgentStarted { ParentAgentSessionId = entry.ParentSessionId, Name = entry.Name })
                .ConfigureAwait(false);
            started = true;
            completed = await ExecuteTurn(child, prompt, followUp).ConfigureAwait(false);

            while (true)
            {
                await entry.Operation.WaitAsync(CancellationToken.None).ConfigureAwait(false);

                if (eventRepository.HasPendingInputs(child.SessionId) && !_lifetime.IsCancellationRequested)
                {
                    _ = entry.Operation.Release();
                    completed = await ExecuteTurn(child, string.Empty, followUp: true).ConfigureAwait(false);
                    continue;
                }

                break;
            }
        }
        catch (Exception failure)
        {
            completed = AgentExecution.Failed(Bounded(failure.Message));
        }

        try
        {
            if (started)
            {
                await Emit(child.SessionId, entry, completed).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            completed = AgentExecution.Failed(Bounded(failure.Message));
        }

        lock (_gate)
        {
            var remaining = _activeByParent[entry.ParentSessionId] - 1;

            if (remaining == 0)
            {
                _ = _activeByParent.Remove(entry.ParentSessionId);
            }
            else
            {
                _activeByParent[entry.ParentSessionId] = remaining;
            }

            entry.Complete(completed);
        }

        if (entry.Operation.CurrentCount == 0)
        {
            _ = entry.Operation.Release();
        }
    }

    private async Task<AgentExecution> ExecuteTurn(AgentSession child, string prompt, bool followUp)
    {
        try
        {
            if (!followUp)
            {
                return Bounded(await child.Run(prompt, _lifetime.Token).ConfigureAwait(false));
            }

            return Bounded(await child.ResultSettled().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return AgentExecution.Canceled();
        }
        catch (Exception failure)
        {
            // An agent execution is a containment boundary: its failure is
            // retained for wait_agent rather than escaping as an unobserved task.
            return AgentExecution.Failed(Bounded(failure.Message));
        }
    }

    private async ValueTask Emit(string sessionId, AgentEntry entry, AgentExecution completed)
    {
        var published = new Event { Id = Identifier.EventId(), AgentSessionId = sessionId };

        if (completed.Status == AgentExecutionStatus.Succeeded)
        {
            published.AgentFinished = new AgentFinished
            {
                ParentAgentSessionId = entry.ParentSessionId,
                Name = entry.Name,
            };
        }
        else
        {
            published.AgentFailed = new AgentFailed
            {
                ParentAgentSessionId = entry.ParentSessionId,
                Name = entry.Name,
                Message = completed.Error,
            };
        }

        await Emit(published).ConfigureAwait(false);
    }

    private async ValueTask Emit(string sessionId, AgentStarted started)
    {
        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = sessionId,
            AgentStarted = started,
        };
        await Emit(published).ConfigureAwait(false);
    }

    private async ValueTask Emit(Event published)
    {
        eventRepository.Append(published, null, null);
        await eventBroker.Publish(published, CancellationToken.None).ConfigureAwait(false);
    }

    private AgentEntry Resolve(AgentSession requester, string sessionIdOrName)
    {
        var sessionId = _entries.ContainsKey(sessionIdOrName)
            ? sessionIdOrName
            : _names.GetValueOrDefault(sessionIdOrName);

        if (sessionId is null
            || !_entries.TryGetValue(sessionId, out var entry)
            || !IsVisibleTo(requester.SessionId, entry))
        {
            throw new AgentRegistryException($"child agent not found: {sessionIdOrName}");
        }

        return entry;
    }

    private bool IsVisibleTo(string requesterSessionId, AgentEntry entry)
    {
        var current = entry;

        while (true)
        {
            if (string.Equals(current.ParentSessionId, requesterSessionId, StringComparison.Ordinal))
            {
                return true;
            }

            if (!_entries.TryGetValue(current.ParentSessionId, out current))
            {
                return false;
            }
        }
    }

    private string UniqueName(string requestedName, string sessionId)
    {
        var basis = Sanitize(requestedName);

        if (basis.Length == 0)
        {
            basis = $"agent-{sessionId[^6..]}";
        }

        var candidate = basis;
        var suffix = 2;

        while (_names.ContainsKey(candidate))
        {
            candidate = $"{basis}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    internal sealed class AgentHandle(AgentSession session, bool running, SemaphoreSlim operation) : IDisposable
    {
        public AgentSession Session { get; } = session;

        public bool Running { get; } = running;

        public void Dispose() => operation.Release();
    }

    private sealed class AgentEntry(AgentSession child, string parentSessionId, string name)
    {
        private TaskCompletionSource<AgentExecution> _completion = CompletionSource();

        public AgentSession Child { get; } = child;

        public string SessionId { get; } = child.SessionId;

        public string ParentSessionId { get; } = parentSessionId;

        public int Depth { get; } = child.Depth;

        public string Name { get; } = name;

        public Task<AgentExecution> Completion => _completion.Task;

        public SemaphoreSlim Operation { get; } = new(1, 1);

        public bool Running { get; private set; }

        public Task Execution { get; private set; } = Task.CompletedTask;

        public void Start(Task execution)
        {
            _completion = CompletionSource();
            Running = true;
            Execution = execution;
        }

        public void Complete(AgentExecution result)
        {
            _completion.SetResult(result);
            Running = false;
            Execution = Task.CompletedTask;
        }

        public AgentTaskResult Result(
            AgentTaskStatus status,
            bool yielded,
            long elapsedMilliseconds,
            string output,
            string error) =>
            new(SessionId, Name, Depth, status, yielded, elapsedMilliseconds, output, error);

        private static TaskCompletionSource<AgentExecution> CompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
