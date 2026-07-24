using System.Text.Json;
using Parrot.Agent;

namespace Parrot.Tools;

// Delegates a subtask to a child agent. The child runs to completion and its
// result comes back as this tool's result, so the parent sees a subtask as one
// tool call while the child's own events stream on the shared session stream.
internal sealed class AgentSpawnTool(UserSession owner, AgentSession session) : ITool
{
    public string Name => "agent_spawn";

    public string Description =>
        "Delegate a self-contained subtask to a child agent. It runs to completion and returns its result.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"prompt":{"type":"string","description":"The subtask for the child agent"}},"required":["prompt"]}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string prompt;

        try
        {
            using var arguments = new ToolArguments(argumentsJson);
            prompt = arguments.RequiredString("prompt");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        if (prompt.Length == 0)
        {
            return "error: no prompt given";
        }

        var child = session.Child(session.Depth + 1);

        if (child is null)
        {
            return "error: subagent depth limit reached";
        }

        // The child joins the user session's agents before it runs, which is
        // what "subagents join later, from the agent side" always meant: the
        // tool is the agent side, and the only place holding both sessions.
        owner.Admit(child);

        return await child.Run(prompt, cancellationToken).ConfigureAwait(false);
    }
}
