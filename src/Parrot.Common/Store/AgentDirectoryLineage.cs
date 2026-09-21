using System.Collections.Immutable;

namespace Parrot.Store;

// Locates every recorded agent's directory by walking the agent tree from its
// roots, in start order, so a re-used name lists its earlier occupants first.
internal sealed class AgentDirectoryLineage
{
    private readonly Dictionary<string, AgentDirectoryOccupants> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImmutableArray<string>> _paths = new(StringComparer.Ordinal);
    private readonly List<string> _unresolved = [];

    private AgentDirectoryLineage()
    {
    }

    public IReadOnlyCollection<AgentDirectoryOccupants> Directories => _directories.Values;

    public IReadOnlyList<string> Unresolved => _unresolved;

    public static AgentDirectoryLineage Resolve(IReadOnlyList<AgentLineageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var lineage = new AgentDirectoryLineage();
        foreach (var record in records)
        {
            if (record.ParentSessionId.Length == 0)
            {
                lineage.Add(record.SessionId, [record.Name]);
            }
            else if (lineage._paths.TryGetValue(record.ParentSessionId, out var parentPath))
            {
                lineage.Add(record.SessionId, parentPath.Add(record.Name));
            }
            else
            {
                lineage._unresolved.Add(record.SessionId);
            }
        }

        return lineage;
    }

    public bool Contains(string sessionId) => _paths.ContainsKey(sessionId);

    public IReadOnlyList<string> OccupantsOf(ImmutableArray<string> namePath, string self) =>
        [
            .. _directories.TryGetValue(Key(namePath), out var directory)
                ? directory.SessionIds.Where(id => !string.Equals(id, self, StringComparison.Ordinal))
                : [],
            self,
        ];

    private static string Key(ImmutableArray<string> namePath) => string.Join('/', namePath);

    private void Add(string sessionId, ImmutableArray<string> namePath)
    {
        _paths[sessionId] = namePath;
        var key = Key(namePath);
        if (!_directories.TryGetValue(key, out var directory))
        {
            directory = new(namePath, []);
            _directories.Add(key, directory);
        }

        directory.SessionIds.Add(sessionId);
    }
}
