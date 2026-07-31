using System.Text;

namespace Parrot.Agent;

internal sealed class AgentScope
{
    private readonly ScopeChange[] _changes;

    private AgentScope(ScopeChange[] changes) => _changes = changes;

    public static AgentScope Empty { get; } = new([]);

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
        return new AgentScope(changes);
    }

    public string Format(int currentDepth)
    {
        if (_changes.Length == 0)
        {
            return string.Empty;
        }

        var prompt = new StringBuilder("## Scope");
        foreach (var change in _changes)
        {
            _ = prompt.Append("\n\n### ");
            var distance = currentDepth - change.Depth;
            if (distance == 0)
            {
                _ = prompt.Append("Self");
            }
            else
            {
                _ = prompt.Append(Ordinal(distance))
                    .Append(" Ancestor (")
                    .Append(change.Name)
                    .Append(')');
            }

            _ = prompt.Append('\n').Append(change.Content);
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
