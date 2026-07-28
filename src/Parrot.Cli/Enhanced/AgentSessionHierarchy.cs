using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentSessionHierarchy
{
    private readonly Dictionary<string, SessionNode> _sessions = new(StringComparer.Ordinal);

    public string? RootSessionId { get; private set; }

    public void Reset()
    {
        RootSessionId = null;
        _sessions.Clear();
    }

    public void Observe(Event published)
    {
        ArgumentNullException.ThrowIfNull(published);

        switch (published.PayloadCase)
        {
            case Event.PayloadOneofCase.AgentStarted:
                Update(published.AgentSessionId, published.AgentStarted.ParentAgentSessionId, published.AgentStarted.Name);
                break;
            case Event.PayloadOneofCase.AgentFinished:
                Update(published.AgentSessionId, published.AgentFinished.ParentAgentSessionId, published.AgentFinished.Name);
                break;
            case Event.PayloadOneofCase.AgentFailed:
                Update(published.AgentSessionId, published.AgentFailed.ParentAgentSessionId, published.AgentFailed.Name);
                break;
            case Event.PayloadOneofCase.TurnStarted when RootSessionId is null && !IsKnownChild(published.AgentSessionId):
                RootSessionId = published.AgentSessionId;
                _ = Get(published.AgentSessionId);
                break;
        }
    }

    public bool IsRoot(string agentSessionId) =>
        RootSessionId is not null
        && string.Equals(RootSessionId, agentSessionId, StringComparison.Ordinal);

    public bool IsChild(string agentSessionId) =>
        !IsRoot(agentSessionId)
        && (RootSessionId is not null || IsKnownChild(agentSessionId));

    public int GetDepth(string agentSessionId)
    {
        if (agentSessionId.Length == 0 || IsRoot(agentSessionId))
        {
            return 0;
        }

        var depth = 0;
        var current = agentSessionId;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current) && _sessions.TryGetValue(current, out var node))
        {
            if (node.ParentSessionId.Length == 0)
            {
                return Math.Max(1, depth);
            }

            depth++;
            if (IsRoot(node.ParentSessionId))
            {
                return depth;
            }

            current = node.ParentSessionId;
        }

        return Math.Max(1, depth);
    }

    public string? GetLabel(string agentSessionId)
    {
        if (agentSessionId.Length == 0 || IsRoot(agentSessionId))
        {
            return null;
        }

        return _sessions.TryGetValue(agentSessionId, out var node) && node.Name.Length > 0
            ? node.Name
            : agentSessionId;
    }

    public bool IsDescendant(string agentSessionId, string ancestorSessionId)
    {
        var current = agentSessionId;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current) && _sessions.TryGetValue(current, out var node))
        {
            if (string.Equals(node.ParentSessionId, ancestorSessionId, StringComparison.Ordinal))
            {
                return true;
            }

            if (node.ParentSessionId.Length == 0)
            {
                return false;
            }

            current = node.ParentSessionId;
        }

        return false;
    }

    public IReadOnlyDictionary<string, int> GetPostOrder(IEnumerable<string> agentSessionIds)
    {
        ArgumentNullException.ThrowIfNull(agentSessionIds);

        var requested = agentSessionIds.ToHashSet(StringComparer.Ordinal);
        var relevant = requested.ToHashSet(StringComparer.Ordinal);
        foreach (var sessionId in requested)
        {
            var current = sessionId;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add(current) && _sessions.TryGetValue(current, out var node) && node.ParentSessionId.Length > 0)
            {
                current = node.ParentSessionId;
                _ = relevant.Add(current);
            }
        }

        var children = relevant.ToDictionary(
            static sessionId => sessionId,
            static _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var sessionId in relevant)
        {
            if (_sessions.TryGetValue(sessionId, out var node)
                && children.TryGetValue(node.ParentSessionId, out var siblings))
            {
                siblings.Add(sessionId);
            }
        }

        foreach (var siblings in children.Values)
        {
            siblings.Sort(StringComparer.Ordinal);
        }

        var roots = relevant.Where(sessionId =>
                !_sessions.TryGetValue(sessionId, out var node)
                || node.ParentSessionId.Length == 0
                || !relevant.Contains(node.ParentSessionId))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (RootSessionId is { } root && roots.Remove(root))
        {
            roots.Add(root);
        }

        var ordered = new List<string>(relevant.Count);
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        void Append(string sessionId)
        {
            if (!seenNodes.Add(sessionId))
            {
                return;
            }

            foreach (var child in children[sessionId])
            {
                Append(child);
            }

            if (requested.Contains(sessionId))
            {
                ordered.Add(sessionId);
            }
        }

        foreach (var sessionId in roots)
        {
            Append(sessionId);
        }

        foreach (var sessionId in relevant.Order(StringComparer.Ordinal))
        {
            Append(sessionId);
        }

        return ordered.Select((sessionId, index) => (sessionId, index))
            .ToDictionary(static item => item.sessionId, static item => item.index, StringComparer.Ordinal);
    }

    private bool IsKnownChild(string agentSessionId) =>
        _sessions.TryGetValue(agentSessionId, out var node) && node.ParentSessionId.Length > 0;

    private SessionNode Get(string agentSessionId)
    {
        if (!_sessions.TryGetValue(agentSessionId, out var node))
        {
            node = new SessionNode();
            _sessions.Add(agentSessionId, node);
        }

        return node;
    }

    private void Update(string agentSessionId, string parentSessionId, string name)
    {
        var node = Get(agentSessionId);
        if (parentSessionId.Length > 0)
        {
            node.ParentSessionId = parentSessionId;
            _ = Get(parentSessionId);
        }

        if (name.Length > 0)
        {
            node.Name = name;
        }
    }

    private sealed class SessionNode
    {
        public string Name { get; set; } = string.Empty;

        public string ParentSessionId { get; set; } = string.Empty;
    }
}
