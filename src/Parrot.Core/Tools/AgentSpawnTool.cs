using System.Text.Json;

namespace Parrot.Tools;

// Delegates a subtask to a child agent. The child runs to completion and its
// result comes back as this tool's result, so the parent sees a subtask as one
// tool call while the child's own events stream on the shared session stream.
internal sealed class AgentSpawnTool : ITool
{
    public string Name => "agent_spawn";

    public string Description =>
        "Delegate a self-contained subtask to a child agent. It runs to completion and returns its result.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"prompt":{"type":"string","description":"The subtask for the child agent"}},"required":["prompt"]}
        """;

    public async Task<string> Execute(
        string argumentsJson, IToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var prompt = ReadString(argumentsJson, "prompt");

        if (prompt.Length == 0)
        {
            return "error: no prompt given";
        }

        return await context.Subagents.Spawn(prompt, context.Depth + 1, cancellationToken).ConfigureAwait(false);
    }

    private static string ReadString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
