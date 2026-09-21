using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Parrot.Agent;
using Parrot.Protocol;

namespace Parrot.Store;

internal sealed partial class EventRepository
{
    public long AppendUsageFact(Event published)
    {
        ArgumentNullException.ThrowIfNull(published);
        if (published.PayloadCase is not (Event.PayloadOneofCase.RequestUsageRecorded or Event.PayloadOneofCase.ToolExecutionStarted))
        {
            throw new ArgumentException("An individual usage fact is required.", nameof(published));
        }

        long revision;
        lock (_database.Gate)
        {
            _historyFile?.ValidateSession(published.AgentSessionId);
            using var transaction = _database.Begin();
            using var existing = _database.Connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT sequence, payload FROM event WHERE id = $id;";
            _ = existing.Parameters.AddWithValue("$id", published.Id);
            using (var reader = existing.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (!Event.Parser.ParseFrom((byte[])reader["payload"]).Equals(published))
                    {
                        throw new InvalidOperationException("A usage fact identity was reused with different content.");
                    }

                    return Convert.ToInt64(reader["sequence"], CultureInfo.InvariantCulture);
                }
            }

            foreach (var (_, previous) in ReadStatisticsEvents(transaction, published.AgentSessionId))
            {
                var duplicateRequest = published.PayloadCase == Event.PayloadOneofCase.RequestUsageRecorded
                    && previous.PayloadCase == Event.PayloadOneofCase.RequestUsageRecorded
                    && string.Equals(previous.RequestUsageRecorded.RequestId, published.RequestUsageRecorded.RequestId, StringComparison.Ordinal);
                var duplicateTool = published.PayloadCase == Event.PayloadOneofCase.ToolExecutionStarted
                    && previous.PayloadCase == Event.PayloadOneofCase.ToolExecutionStarted
                    && string.Equals(previous.ToolExecutionStarted.ToolCallId, published.ToolExecutionStarted.ToolCallId, StringComparison.Ordinal);
                if (duplicateRequest || duplicateTool)
                {
                    throw new InvalidOperationException("A request or tool execution already has a usage fact with another event identity.");
                }
            }

            revision = Record(transaction, published);
            if (published.PayloadCase == Event.PayloadOneofCase.RequestUsageRecorded)
            {
                EnsureAgentHistoryProjection(transaction);
                using var anchor = _database.Connection.CreateCommand();
                anchor.Transaction = transaction;
                anchor.CommandText =
                    "INSERT INTO request_history_anchor (event_sequence, agent_session, history_sequence) "
                    + "SELECT $event, $session, COALESCE(MAX(sequence), 0) FROM agent_history WHERE agent_session = $session;";
                _ = anchor.Parameters.AddWithValue("$event", revision);
                _ = anchor.Parameters.AddWithValue("$session", published.AgentSessionId);
                _ = anchor.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        RefreshAgentHistory(published.AgentSessionId);
        return revision;
    }

    public RequestUsageRecorded? FindRequestUsage(string agentSessionId, string toolCallId)
    {
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            var request = ReadStatisticsEvents(transaction, agentSessionId)
                .Where(static fact => fact.Published.PayloadCase == Event.PayloadOneofCase.RequestUsageRecorded)
                .Select(static fact => fact.Published.RequestUsageRecorded)
                .LastOrDefault(request => request.ToolCallIds.Contains(toolCallId));
            transaction.Commit();
            return request;
        }
    }

    public IReadOnlyList<AgentLineageRecord> AgentLineage()
    {
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            var lineage = ReadStatisticsEvents(transaction, null)
                .Where(static fact => fact.Published.PayloadCase == Event.PayloadOneofCase.AgentStarted)
                .DistinctBy(static fact => fact.Published.AgentSessionId, StringComparer.Ordinal)
                .Select(static fact => new AgentLineageRecord(
                    fact.Published.AgentSessionId,
                    fact.Published.AgentStarted.ParentAgentSessionId,
                    fact.Published.AgentStarted.Name))
                .ToArray();
            transaction.Commit();
            return lineage;
        }
    }

    public AgentStatisticsReplay ReplayStatistics()
    {
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            var facts = ReadStatisticsEvents(transaction, null);
            var parents = new Dictionary<string, string>(StringComparer.Ordinal);
            var agents = new Dictionary<string, AgentSessionStatistics>(StringComparer.Ordinal);
            var baselines = new Dictionary<string, (long Revision, AgentStatisticsUpdatedEvent Statistics)>(StringComparer.Ordinal);
            foreach (var (revision, published) in facts)
            {
                _ = agents.TryAdd(published.AgentSessionId, new(AgentSessionStatisticsSnapshot.Empty));
                switch (published.PayloadCase)
                {
                    case Event.PayloadOneofCase.AgentStarted:
                        RecordParent(parents, published.AgentSessionId, published.AgentStarted.ParentAgentSessionId);
                        break;
                    case Event.PayloadOneofCase.AgentFinished:
                        RecordParent(parents, published.AgentSessionId, published.AgentFinished.ParentAgentSessionId);
                        break;
                    case Event.PayloadOneofCase.AgentFailed:
                        RecordParent(parents, published.AgentSessionId, published.AgentFailed.ParentAgentSessionId);
                        break;
                    case Event.PayloadOneofCase.RequestUsageRecorded:
                        RecordParent(parents, published.AgentSessionId, published.RequestUsageRecorded.ParentAgentSessionId);
                        break;
                    case Event.PayloadOneofCase.ToolExecutionStarted:
                        RecordParent(parents, published.AgentSessionId, published.ToolExecutionStarted.ParentAgentSessionId);
                        break;
                    case Event.PayloadOneofCase.AgentStatisticsUpdated:
                        baselines[published.AgentSessionId] = (revision, published.AgentStatisticsUpdated);
                        break;
                }
            }

            foreach (var (agentId, baseline) in baselines)
            {
                var value = baseline.Statistics;
                agents[agentId].AddOwn(new(
                    AgentUsageKey.Legacy,
                    new(value.InputTokens, value.CachedInputTokens, value.OutputTokens, 0, value.InputCost, value.OutputCost),
                    value.ContextSize,
                    value.ContextLimit));
            }

            var requests = new HashSet<(string Agent, string Request)>();
            var tools = new HashSet<(string Agent, string Call)>();
            foreach (var (revision, published) in facts)
            {
                var agentId = published.AgentSessionId;
                switch (published.PayloadCase)
                {
                    case Event.PayloadOneofCase.RequestUsageRecorded:
                        var request = published.RequestUsageRecorded;
                        if (requests.Add((agentId, request.RequestId))
                            && (!baselines.TryGetValue(agentId, out var baseline) || revision > baseline.Revision))
                        {
                            agents[agentId].AddOwn(ReadRequestIncrement(request));
                        }

                        break;
                    case Event.PayloadOneofCase.ToolExecutionStarted:
                        var tool = published.ToolExecutionStarted;
                        if (tools.Add((agentId, tool.ToolCallId)))
                        {
                            agents[agentId].AddOwn(AgentUsageIncrement.FromToolStart(
                                new(tool.Provider, tool.Model, tool.HasEffort ? tool.Effort : null)));
                        }

                        break;
                }
            }

            var incompleteLegacyTools = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            var legacyStarts = facts
                .Where(static fact => fact.Published.PayloadCase == Event.PayloadOneofCase.ToolStarted)
                .Select(static fact => (Agent: fact.Published.AgentSessionId, Call: fact.Published.ToolStarted.ToolCallId))
                .ToHashSet();
            foreach (var (_, published) in facts)
            {
                if (published.PayloadCase == Event.PayloadOneofCase.ToolFinished
                    && legacyStarts.Contains((published.AgentSessionId, published.ToolFinished.ToolCallId))
                    && tools.Add((published.AgentSessionId, published.ToolFinished.ToolCallId)))
                {
                    agents[published.AgentSessionId].AddOwn(AgentUsageIncrement.FromToolStart(AgentUsageKey.Legacy));
                }
            }

            foreach (var (agentId, callId) in legacyStarts)
            {
                if (!tools.Contains((agentId, callId)))
                {
                    _ = incompleteLegacyTools.Add(agentId);
                }
            }

            using (var main = _database.Connection.CreateCommand())
            {
                main.Transaction = transaction;
                main.CommandText = "SELECT agent_session FROM session_state LIMIT 1;";
                if (main.ExecuteScalar() is string mainAgentId)
                {
                    RecordParent(parents, mainAgentId, string.Empty);
                }
            }

            foreach (var (agentId, statistics) in agents)
            {
                if (statistics.Capture().Self.Models.Count > 0 && !parents.ContainsKey(agentId))
                {
                    throw new InvalidOperationException($"Historical usage has no parent lineage for agent {agentId}.");
                }
            }

            foreach (var parent in parents.Values.Where(static parent => parent.Length > 0))
            {
                if (!parents.ContainsKey(parent))
                {
                    throw new InvalidOperationException($"Historical ancestor {parent} has no parent lineage.");
                }

                _ = agents.TryAdd(parent, new(AgentSessionStatisticsSnapshot.Empty));
            }

            foreach (var (agentId, statistics) in agents)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal) { agentId };
                var ancestor = agentId;
                while (parents.TryGetValue(ancestor, out var parent) && parent.Length > 0)
                {
                    if (!visited.Add(parent))
                    {
                        throw new InvalidOperationException("Historical agent lineage contains a cycle.");
                    }

                    foreach (var (key, totals) in statistics.Capture().Self.Models)
                    {
                        agents[parent].AddDescendant(new(key, totals, null, null));
                    }

                    ancestor = parent;
                }
            }

            transaction.Commit();
            return new(
                facts.Count == 0 ? 0 : facts[^1].Revision,
                agents.ToImmutableDictionary(static pair => pair.Key, static pair => pair.Value.Capture(), StringComparer.Ordinal),
                parents.ToImmutableDictionary(StringComparer.Ordinal),
                incompleteLegacyTools.ToImmutable());
        }
    }

    private static AgentUsageIncrement ReadRequestIncrement(RequestUsageRecorded request) => new(
        new(request.Provider, request.Model, request.HasEffort ? request.Effort : null),
        new(request.InputTokens, request.CachedInputTokens, request.OutputTokens, 0, request.InputCost, request.OutputCost),
        request.ContextSize,
        request.ContextLimit);

    private static void RecordParent(Dictionary<string, string> parents, string agentId, string parent)
    {
        if (parents.TryGetValue(agentId, out var existing) && !string.Equals(existing, parent, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Historical agent lineage contains conflicting parents.");
        }

        parents[agentId] = parent;
    }

    private List<(long Revision, Event Published)> ReadStatisticsEvents(SqliteTransaction transaction, string? agentSessionId)
    {
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT sequence, payload FROM event WHERE $session IS NULL OR agent_session = $session ORDER BY sequence;";
        _ = read.Parameters.AddWithValue("$session", (object?)agentSessionId ?? DBNull.Value);
        using var reader = read.ExecuteReader();
        var facts = new List<(long Revision, Event Published)>();
        while (reader.Read())
        {
            facts.Add((
                Convert.ToInt64(reader["sequence"], CultureInfo.InvariantCulture),
                Event.Parser.ParseFrom((byte[])reader["payload"])));
        }

        return facts;
    }

    private AgentHistoryEntry[] IncludeRequestHistory(
        SqliteTransaction transaction,
        string agentSessionId,
        IReadOnlyList<AgentHistoryEntry> entries)
    {
        var facts = ReadStatisticsEvents(transaction, agentSessionId);
        var toolCounts = facts
            .Where(static fact => fact.Published.PayloadCase == Event.PayloadOneofCase.ToolExecutionStarted)
            .Select(static fact => fact.Published.ToolExecutionStarted)
            .DistinctBy(static tool => tool.ToolCallId)
            .GroupBy(static tool => tool.RequestId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.LongCount(), StringComparer.Ordinal);
        var anchors = new Dictionary<long, long>();
        using (var read = _database.Connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT event_sequence, history_sequence FROM request_history_anchor WHERE agent_session = $session;";
            _ = read.Parameters.AddWithValue("$session", agentSessionId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                anchors.Add(
                    Convert.ToInt64(reader["event_sequence"], CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader["history_sequence"], CultureInfo.InvariantCulture));
            }
        }

        var requests = facts
            .Where(static fact => fact.Published.PayloadCase == Event.PayloadOneofCase.RequestUsageRecorded)
            .DistinctBy(static fact => fact.Published.RequestUsageRecorded.RequestId)
            .Select(fact =>
            {
                var request = fact.Published.RequestUsageRecorded;
                return new AgentHistoryRequestEntry(
                    anchors.GetValueOrDefault(fact.Revision),
                    fact.Revision,
                    request.RequestId,
                    request.Provider,
                    request.Model,
                    request.HasEffort ? request.Effort : null,
                    request.InputTokens,
                    request.CachedInputTokens,
                    request.OutputTokens,
                    request.InputCost,
                    request.OutputCost,
                    toolCounts.GetValueOrDefault(request.RequestId));
            });
        return [.. entries.Concat(requests).OrderBy(static entry => entry.Sequence)];
    }
}
