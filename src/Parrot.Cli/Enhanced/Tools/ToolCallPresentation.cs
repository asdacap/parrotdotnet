namespace Parrot.Cli.Enhanced.Tools;

internal readonly record struct ToolCallPresentation(
    string Owner,
    string ToolName,
    string ArgumentsJson,
    Func<string, string> AgentReferenceResolver)
{
    public ToolCallPresentation(string owner, string toolName, string argumentsJson)
        : this(owner, toolName, argumentsJson, static reference => reference)
    {
    }

    public string ResolveAgentReference(string reference) => AgentReferenceResolver(reference);
}
