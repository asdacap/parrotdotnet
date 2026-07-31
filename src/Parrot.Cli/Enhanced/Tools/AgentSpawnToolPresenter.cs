using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class AgentSpawnToolPresenter : IToolPresenter
{
    public string ToolName => "agent_spawn";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        SuccessIcon = "♟",
        TerminalOnly = true,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var label = arguments.RootElement.TryGetProperty("name", out var name)
            && name.GetString() is { Length: > 0 } value
                ? $"{call.Owner}: Start agent {value}"
                : $"{call.Owner}: Start agent";
        return new ToolLiveValue(label, [], Metadata, frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        var label = arguments.RootElement.TryGetProperty("name", out var name)
            && name.GetString() is { Length: > 0 } value
                ? $"{call.Owner}: Start agent {value}"
                : $"{call.Owner}: Start agent";
        var block = status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure
            ? terminal.DescribeBlock(ToolBlockKind.None)
            : ToolBlock.FromCompletedInput(CompletedInput(arguments.RootElement));
        return new ToolScrollbackValue(label, block, status, Metadata);
    }

    private static string CompletedInput(JsonElement arguments)
    {
        var values = new List<string>();
        AddScalar(values, arguments, "name");
        AddScalar(values, arguments, "agent");
        AddScalar(values, arguments, "model");
        AddScalar(values, arguments, "scope");
        if (arguments.GetProperty("prompt").GetString() is { Length: > 0 } prompt)
        {
            values.Add(prompt.Contains('\n', StringComparison.Ordinal)
                ? $"prompt: |-\n{string.Join('\n', prompt.Split('\n').Select(line => $"  {line}"))}"
                : $"prompt: {prompt}");
        }

        return string.Join('\n', values);
    }

    private static void AddScalar(List<string> values, JsonElement arguments, string name)
    {
        if (arguments.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { Length: > 0 } value)
        {
            values.Add($"{name}: {value}");
        }
    }
}
