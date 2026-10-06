using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class GetAgentTasksToolPresenter(IToolPresenter generic) : IToolPresenter
{
    public string ToolName => "get_agent_tasks";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue(Label(call), ToolBlock.Empty, Metadata, frame);

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var label = Label(call);
        var status = terminal.ResolveStatus();
        if (status != ToolTerminalStatus.Succeeded || !terminal.ResultPresent)
        {
            return generic.PresentTerminal(call, terminal);
        }

        using var document = JsonDocument.Parse(terminal.Result);
        var rows = new List<string>();
        if (ReadName(call) is null)
        {
            var tasks = document.RootElement.EnumerateArray().ToArray();
            foreach (var task in tasks)
            {
                if (IsHidden(task))
                {
                    continue;
                }

                var state = ReadState(task);
                rows.Add($"{Icon(state)} {state} · {ReadString(task, "name")} · {ReadString(task, "description")}");
            }

            if (rows.Count == 0)
            {
                if (tasks.Length > 0)
                {
                    return null;
                }

                rows.Add("No agent tasks.");
            }
        }
        else
        {
            var task = document.RootElement;
            if (IsHidden(task))
            {
                return null;
            }

            rows.Add($"{ReadString(task, "name")} · {ReadState(task)}");
            AppendDefinition(task, string.Empty, rows, true);
        }

        return new ToolScrollbackValue(label, ToolBlock.FromDetails(rows), status, Metadata);
    }

    private static string Label(ToolCallPresentation call) =>
        ReadName(call) is { } name ? $"Agent task · {name}" : "Agent tasks";

    private static string? ReadName(ToolCallPresentation call)
    {
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("get_agent_tasks requires an object.");
        }

        if (!root.TryGetProperty("name", out var name))
        {
            return null;
        }

        if (name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
        {
            throw new FormatException("get_agent_tasks requires a nonblank name.");
        }

        return name.GetString();
    }

    private static void AppendDefinition(JsonElement task, string indent, List<string> rows, bool withExecution)
    {
        if (withExecution)
        {
            rows.Add($"Description: {ReadString(task, "description")}");
            AppendOptional(task, "agent_name", "Agent", indent, rows);
        }

        var dependencies = task.GetProperty("dependencies").EnumerateArray()
            .Select(dependency => dependency.GetString() ?? throw new FormatException("Task dependencies must be strings."))
            .ToArray();
        if (dependencies.Length > 0)
        {
            rows.Add($"{indent}Dependencies: {string.Join(", ", dependencies)}");
        }

        AppendOptional(task, "model", "Model", indent, rows);
        rows.Add($"{indent}Acceptance criteria: {ReadString(task, "acceptance_criteria")}");
        if (withExecution)
        {
            AppendOptional(task, "result", "Result", indent, rows);
            AppendOptional(task, "failure", "Failure", indent, rows);
        }
        else if (task.TryGetProperty("state", out _) || task.TryGetProperty("result", out _) || task.TryGetProperty("failure", out _))
        {
            throw new FormatException("Declared child definitions must not contain execution state.");
        }

        var payload = task.GetProperty("payload");
        if (payload.ValueKind == JsonValueKind.String)
        {
            rows.Add($"{indent}Instruction: {payload.GetString()}");
            return;
        }

        var children = payload.EnumerateArray().Where(static child => !IsHidden(child)).ToArray();
        if (children.Length > 0)
        {
            rows.Add($"{indent}Declared child definitions:");
        }

        foreach (var child in children)
        {
            rows.Add($"{indent}  {ReadString(child, "name")} · {ReadString(child, "description")}");
            AppendDefinition(child, indent + "    ", rows, false);
        }
    }

    private static bool IsHidden(JsonElement task) =>
        task.TryGetProperty("hidden", out var hidden) && hidden.GetBoolean();

    private static void AppendOptional(JsonElement task, string property, string label, string indent, List<string> rows)
    {
        if (task.TryGetProperty(property, out _))
        {
            rows.Add($"{indent}{label}: {ReadString(task, property)}");
        }
    }

    private static string ReadString(JsonElement task, string property) =>
        task.GetProperty(property).GetString() ?? throw new FormatException($"Task {property} must be a string.");

    private static string ReadState(JsonElement task)
    {
        var state = ReadString(task, "state");
        _ = Icon(state);
        return state;
    }

    private static string Icon(string state) => state switch
    {
        "pending" => "○",
        "running" => "◐",
        "succeeded" => "✓",
        "failed" => "✗",
        "canceled" => "■",
        _ => throw new FormatException("Unsupported task execution state."),
    };
}
