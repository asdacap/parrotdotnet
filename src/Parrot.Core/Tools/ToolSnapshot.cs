using Parrot.Llm;

namespace Parrot.Tools;

// An immutable per-turn view over agent-session tool instances (principle 4).
// No mutators, so "do not change the tools mid-turn" is a compiler guarantee
// rather than a comment.
internal sealed class ToolSnapshot(IReadOnlyList<ITool> tools)
{
    private readonly ITool[] _tools = [.. tools];

    public IReadOnlyList<ITool> Tools => Array.AsReadOnly(_tools);

    public IReadOnlyList<LLMToolDefinition> Definitions =>
        [.. Tools.Select(tool => new LLMToolDefinition(tool.Name, tool.Description, tool.ParametersJson))];

    public ITool? Find(string name) =>
        Tools.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    public ToolSnapshot Only(IReadOnlyList<string>? allowedTools) => allowedTools is null
        ? this
        : new ToolSnapshot([.. _tools.Where(tool => allowedTools.Contains(tool.Name, StringComparer.Ordinal))]);

    public ToolSnapshot Without(IReadOnlyList<string> disabledTools)
    {
        ArgumentNullException.ThrowIfNull(disabledTools);
        return disabledTools.Count == 0
            ? this
            : new ToolSnapshot([.. _tools.Where(tool => !disabledTools.Contains(tool.Name, StringComparer.Ordinal))]);
    }
}
