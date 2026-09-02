using System.Text;
using Parrot.Llm;

namespace Parrot.Agent;

internal sealed class AgentSessionActivity(TimeProvider timeProvider)
{
    private const int RecentEntryLimit = 5;

    private readonly Lock _gate = new();
    private readonly SortedDictionary<long, string> _activeTools = [];
    private readonly Dictionary<string, StringBuilder> _namedSummaries = new(StringComparer.Ordinal);
    private readonly List<RecentEntry> _recent = [];
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private StringBuilder? _unnamedSummary;
    private long? _currentProviderRequestStarted;
    private TimeSpan? _lastProviderRequestDuration;
    private long? _latestProviderActivity;
    private long? _requestSessionStarted;
    private TimeSpan? _requestSessionDuration;
    private long _requestSessionExecution;
    private long _toolExecution;
    private DrainState _state;
    private AgentExecution? _terminalOutcome;

    public void ObserveProviderEvent(LLMEvent observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Kind == LLMEventKind.Completed)
        {
            return;
        }

        lock (_gate)
        {
            var timestamp = _timeProvider.GetTimestamp();
            _latestProviderActivity = timestamp;
            if (observed is
                {
                    Kind: LLMEventKind.ReasoningDelta,
                    ReasoningKind: LLMReasoningKind.Summary,
                })
            {
                ObserveSummary(observed, timestamp);
            }
        }
    }

    public void BeginProviderRequest()
    {
        lock (_gate)
        {
            _currentProviderRequestStarted = _timeProvider.GetTimestamp();
        }
    }

    public void FinishProviderRequest()
    {
        lock (_gate)
        {
            if (_currentProviderRequestStarted is { } started)
            {
                _lastProviderRequestDuration = GetElapsedTime(started, _timeProvider.GetTimestamp());
                _currentProviderRequestStarted = null;
            }

            _namedSummaries.Clear();
            _unnamedSummary = null;
        }
    }

    public void RecordAssistantMessage(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        lock (_gate)
        {
            AppendRecent(AgentSessionActivityEntryKind.AssistantMessage, content, _timeProvider.GetTimestamp());
        }
    }

    public long BeginTool(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            _toolExecution++;
            _activeTools.Add(_toolExecution, name);
            return _toolExecution;
        }
    }

    public void FinishTool(long execution)
    {
        lock (_gate)
        {
            _ = _activeTools.Remove(execution);
        }
    }

    public void ChangeState(DrainState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }

    public long BeginExecution()
    {
        lock (_gate)
        {
            _requestSessionExecution++;
            _requestSessionStarted = _timeProvider.GetTimestamp();
            _requestSessionDuration = null;
            _terminalOutcome = null;
            return _requestSessionExecution;
        }
    }

    public void FinishExecution(long execution, AgentExecution outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        lock (_gate)
        {
            if (_requestSessionExecution != execution)
            {
                return;
            }

            if (_requestSessionStarted is { } started)
            {
                _requestSessionDuration = GetElapsedTime(started, _timeProvider.GetTimestamp());
                _requestSessionStarted = null;
            }

            _terminalOutcome = outcome;
        }
    }

    public AgentSessionActivitySnapshot Capture()
    {
        lock (_gate)
        {
            var timestamp = _timeProvider.GetTimestamp();
            var recent = new AgentSessionActivityEntrySnapshot[_recent.Count];
            for (var index = 0; index < _recent.Count; index++)
            {
                var entry = _recent[index];
                recent[index] = new AgentSessionActivityEntrySnapshot(
                    entry.Kind,
                    entry.Content,
                    GetElapsedTime(entry.Timestamp, timestamp));
            }

            TimeSpan? providerActivityAge = _latestProviderActivity is { } providerActivity
                ? GetElapsedTime(providerActivity, timestamp)
                : null;
            var requestSessionDuration = _requestSessionStarted is { } requestStarted
                ? GetElapsedTime(requestStarted, timestamp)
                : _requestSessionDuration;
            TimeSpan? currentProviderRequestDuration = _currentProviderRequestStarted is { } providerStarted
                ? GetElapsedTime(providerStarted, timestamp)
                : null;
            return new AgentSessionActivitySnapshot(
                _state,
                _activeTools.Count == 0 ? null : _activeTools.Values.Last(),
                requestSessionDuration,
                currentProviderRequestDuration,
                _lastProviderRequestDuration,
                providerActivityAge,
                _terminalOutcome,
                Array.AsReadOnly(recent));
        }
    }

    private void ObserveSummary(LLMEvent observed, long timestamp)
    {
        var summary = observed.ReasoningPartId.Length == 0
            ? ObserveUnnamedSummary(observed)
            : ObserveNamedSummary(observed);
        if (summary is not null && !string.IsNullOrWhiteSpace(summary))
        {
            AppendRecent(AgentSessionActivityEntryKind.ReasoningSummary, summary, timestamp);
        }
    }

    private string? ObserveNamedSummary(LLMEvent observed)
    {
        if (!_namedSummaries.TryGetValue(observed.ReasoningPartId, out var summary))
        {
            summary = new StringBuilder();
            _namedSummaries.Add(observed.ReasoningPartId, summary);
        }

        _ = summary.Append(observed.Text);
        if (!observed.ReasoningCompleted)
        {
            return null;
        }

        _ = _namedSummaries.Remove(observed.ReasoningPartId);
        return summary.ToString();
    }

    private string? ObserveUnnamedSummary(LLMEvent observed)
    {
        _unnamedSummary ??= new StringBuilder();
        _ = _unnamedSummary.Append(observed.Text);
        if (!observed.ReasoningCompleted)
        {
            return null;
        }

        var completed = _unnamedSummary.ToString();
        _unnamedSummary = null;
        return completed;
    }

    private void AppendRecent(AgentSessionActivityEntryKind kind, string content, long timestamp)
    {
        _recent.Add(new RecentEntry(kind, content, timestamp));
        if (_recent.Count > RecentEntryLimit)
        {
            _recent.RemoveAt(0);
        }
    }

    private TimeSpan GetElapsedTime(long timestamp, long observedAt)
    {
        var elapsed = _timeProvider.GetElapsedTime(timestamp, observedAt);
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private sealed record RecentEntry(AgentSessionActivityEntryKind Kind, string Content, long Timestamp);
}
