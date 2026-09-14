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
        ToolDefinitionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(supported);
        ArgumentNullException.ThrowIfNull(catalog);
        if (tools.Count != supported.Count)
        {
            throw new ArgumentException("Tool support flags must match the tool inventory.", nameof(supported));
        }

        var definitions = catalog.Document(tools);
        var entries = new List<ToolEntry>(tools.Count);
        for (var index = 0; index < tools.Count; index++)
        {
            if (supported[index])
            {
                entries.Add(new ToolEntry(tools[index], definitions[index]));
            }
        }

        return new ToolSnapshot(entries);
    }

    public ITool? Find(string name) =>
        _entries.FirstOrDefault(entry => string.Equals(entry.Tool.Name, name, StringComparison.Ordinal))?.Tool;

    public ToolSnapshot Only(IReadOnlyList<string>? allowedTools) => allowedTools is null
        ? this
        : new ToolSnapshot([.. _entries.Where(entry => allowedTools.Contains(entry.Tool.Name, StringComparer.Ordinal))]);

    public ToolSnapshot Without(IReadOnlyList<string> disabledTools)
    {
        ArgumentNullException.ThrowIfNull(disabledTools);
        return disabledTools.Count == 0
            ? this
            : new ToolSnapshot([.. _entries.Where(entry => !disabledTools.Contains(entry.Tool.Name, StringComparer.Ordinal))]);
    }

    public ToolSnapshot EnabledAfterInterruption() =>
        new([.. _entries.Where(entry => entry.Tool.IsEnabledAfterInterruption)]);

    private sealed record ToolEntry(ITool Tool, LLMToolDefinition Definition);
}
