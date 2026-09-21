using Parrot.Process;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class AgentPathEnvironment(UserSessionResources resources, AgentScratchDirectory scratch) : IAgentPathEnvironment
{
    private readonly ProcessEnvironmentOverrides _defaults = new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WORKDIR"] = resources.Workspace.LaunchDirectory,
        ["SCRATCH_DIR"] = resources.ScratchDirectory,
        ["AGENT_SCRATCH_DIR"] = scratch.ScratchPath,
        ["AGENT_HISTORY_DIR"] = Path.GetDirectoryName(scratch.HistoryPath)
            ?? throw new InvalidOperationException("The agent history file has no parent directory."),
    });

    public IReadOnlyList<KeyValuePair<string, string>> Materialize() => _defaults.Entries;

    public ProcessEnvironmentOverrides Merge(ProcessEnvironmentOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var entries = _defaults.Entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (var entry in overrides.Entries)
        {
            entries[entry.Key] = entry.Value;
        }

        return new ProcessEnvironmentOverrides(entries);
    }
}
