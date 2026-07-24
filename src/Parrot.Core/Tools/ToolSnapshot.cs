using Parrot.Llm;

namespace Parrot.Tools;

// An immutable set of tools materialised once per turn (principle 4). No
// mutators, so "do not change the tools mid-turn" is a compiler guarantee
// rather than a comment.
internal sealed class ToolSnapshot(IReadOnlyList<ITool> tools)
{
    public IReadOnlyList<ITool> Tools { get; } = tools;

    public IReadOnlyList<LLMToolDefinition> Definitions =>
        [.. Tools.Select(tool => new LLMToolDefinition(tool.Name, tool.Description, tool.ParametersJson))];

    public ITool? Find(string name) =>
        Tools.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
}
