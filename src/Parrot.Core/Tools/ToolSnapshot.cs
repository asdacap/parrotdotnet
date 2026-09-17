using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class ToolSnapshot
{
    private readonly ToolEntry[] _entries;

    private ToolSnapshot(IReadOnlyList<ToolEntry> entries) => _entries = [.. entries];

    public static ToolSnapshot Empty { get; } = new([]);

    public IReadOnlyList<ITool> Tools => Array.AsReadOnly(_entries.Select(entry => entry.Tool).ToArray());

    public IReadOnlyList<LLMToolDefinition> Definitions =>
        Array.AsReadOnly(_entries.Select(entry => entry.Definition).ToArray());

    public static ToolSnapshot Document(
        IReadOnlyList<ITool> tools,
        IReadOnlyList<bool> supported,
        IReadOnlyList<IToolDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(supported);
        ArgumentNullException.ThrowIfNull(definitions);
        if (tools.Count != supported.Count || tools.Count != definitions.Count)
        {
            throw new ArgumentException("Tool support flags and definitions must match the tool inventory.", nameof(tools));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<ToolEntry>(tools.Count);
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            if (!names.Add(tool.Name))
            {
                throw new InvalidDataException($"tool '{tool.Name}' is registered more than once");
            }

            if (supported[index])
            {
                var definition = definitions[index];
                entries.Add(new ToolEntry(tool, new LLMToolDefinition(tool.Name, definition.Description, definition.ParametersJson)));
            }
        }

        return new ToolSnapshot(entries);
    }

    public ITool? Find(string name) =>
        _entries.FirstOrDefault(entry => string.Equals(entry.Tool.Name, name, StringComparison.Ordinal))?.Tool;

    public ToolSnapshot PermittedBy(IAgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ToolSnapshot([.. _entries.Where(entry => profile.IsToolPermitted(entry.Tool.Name))]);
    }

    public ToolSnapshot EnabledAfterInterruption() =>
        new([.. _entries.Where(entry => entry.Tool.IsEnabledAfterInterruption)]);

    private sealed record ToolEntry(ITool Tool, LLMToolDefinition Definition);
}
