using Parrot.Config;

namespace Parrot.Agent;

internal sealed class AgentScope
{
    private readonly ScopeChange[] _changes;

    private AgentScope(ScopeChange[] changes, PromptTemplateCatalog promptTemplates)
    {
        _changes = changes;
        PromptTemplates = promptTemplates;
    }

    public PromptTemplateCatalog PromptTemplates { get; }

    public static AgentScope Empty(PromptTemplateCatalog promptTemplates) => new([], promptTemplates);

    public AgentScope DeriveChild(string name, int depth, string requestedScope)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(requestedScope);

        if (requestedScope.Length == 0
            || (_changes.Length > 0
                && string.Equals(_changes[^1].Content, requestedScope, StringComparison.Ordinal)))
        {
            return this;
        }

        var changes = new ScopeChange[_changes.Length + 1];
        _changes.CopyTo(changes, 0);
        changes[^1] = new ScopeChange(name, depth, requestedScope);
        return new AgentScope(changes, PromptTemplates);
    }

    public string Format(int currentDepth)
    {
        if (_changes.Length == 0)
        {
            return string.Empty;
        }

        var prompt = new System.Text.StringBuilder();
        for (var index = 0; index < _changes.Length; index++)
        {
            var change = _changes[index];
            var distance = currentDepth - change.Depth;
            var label = distance == 0
                ? "Self"
                : $"{Ordinal(distance)} Ancestor ({change.Name})";
            _ = prompt.Append(PromptTemplates.Render(
                "system.agent-scope",
                [
                    new("heading", index == 0 ? "## Scope" : string.Empty),
                    new("label", label),
                    new("content", change.Content),
                ]));
        }

        return prompt.ToString();
    }

    private static string Ordinal(int value)
    {
        var remainder = value % 100;
        var suffix = remainder is >= 11 and <= 13
            ? "th"
            : (value % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th",
            };
        return $"{value}{suffix}";
    }

    private sealed record ScopeChange(string Name, int Depth, string Content);
}
