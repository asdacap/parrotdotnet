namespace Parrot.Cli.Enhanced.Tools;

internal readonly record struct ToolCallPresentation(
    string ToolName,
    string ArgumentsJson,
    Func<string, string> AgentReferenceResolver)
{
    public ToolCallPresentation(string toolName, string argumentsJson)
        : this(toolName, argumentsJson, static reference => reference)
    {
    }

    public string ResolveAgentReference(string reference) => AgentReferenceResolver(reference);
}
