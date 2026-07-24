using System.Diagnostics;
using System.Text;
using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

// Owns every child agent in one user session. Entries are retained after they
// finish so waiting is repeatable, while only running entries consume the
// per-parent concurrency limit. _gate protects admission, names, and active counts.
internal sealed class AgentRegistry(
    IAgentSessionFactory agentSessions,
    EventBroker eventBroker,
    EventRepository eventRepository,
    CancellationToken lifetime) : IAsyncDisposable
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
            var child = agentSessions.Create(
                identity,
                parent.Provider,
                parent.Model,
                eventBroker,
                eventRepository,
                _lifetime.Token);
            var entry = new AgentEntry(child, parent.SessionId, name);

            _entries.Add(sessionId, entry);
            _names.Add(name, sessionId);
            _activeByParent[parent.SessionId] = parentActive + 1;
            entry.Start(Execute(entry, child, prompt));

            return entry.Result(
                AgentTaskStatus.Running,
                yielded: false,
                elapsedMilliseconds: 0,
                string.Empty,
                string.Empty);
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

        lock (_gate)
        {
            entry = Resolve(requester, sessionIdOrName);
        }

        var started = Stopwatch.GetTimestamp();

        if (yieldAfterMilliseconds == 0)
        {
            var completed = await entry.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Terminal(entry, completed, Elapsed(started));
        }

        using var yielded = new CancellationTokenSource(TimeSpan.FromMilliseconds(yieldAfterMilliseconds));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, yielded.Token);

        try
        {
            var completed = await entry.Completion.WaitAsync(wait.Token).ConfigureAwait(false);
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

    private async Task Execute(AgentEntry entry, AgentSession child, string prompt)
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
            completed = await child.Run(prompt, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            completed = AgentExecution.Canceled();
        }
        catch (Exception failure)
        {
            // An agent execution is a containment boundary: its failure is
            // retained for wait_agent rather than escaping as an unobserved task.
            completed = AgentExecution.Failed(failure.Message);
        }

        completed = completed with
        {
            Output = Bounded(completed.Output),
            Error = Bounded(completed.Error),
        };

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

    private sealed class AgentEntry(AgentSession child, string parentSessionId, string name)
    {
        private readonly TaskCompletionSource<AgentExecution> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SessionId { get; } = child.SessionId;

        public string ParentSessionId { get; } = parentSessionId;

        public int Depth { get; } = child.Depth;

        public string Name { get; } = name;

        public Task<AgentExecution> Completion => _completion.Task;

        public Task Execution { get; private set; } = Task.CompletedTask;

        public void Start(Task execution) => Execution = execution;

        public void Complete(AgentExecution result)
        {
            _completion.SetResult(result);
            Execution = Task.CompletedTask;
        }

        public AgentTaskResult Result(
            AgentTaskStatus status,
            bool yielded,
            long elapsedMilliseconds,
            string output,
            string error) =>
            new(SessionId, Name, Depth, status, yielded, elapsedMilliseconds, output, error);
    }
}
