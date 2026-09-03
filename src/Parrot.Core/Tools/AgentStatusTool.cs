using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class AgentStatusTool(
    AgentResolver resolver,
    ChildRegistry children,
    ShellProcessOwners processes) : ITool
{
    public string Name => "agent_status";

    public Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _ = cancellationToken;

        string sessionId;
        try
        {
            var input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                AgentProcessToolJsonContext.Default.AgentStatusToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            sessionId = input.SessionId ?? throw new FormatException("Tool arguments require a string 'session_id'.");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, failure.Message));
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, "no child session given"));
        }

        try
        {
            var child = resolver.ResolveStatusTarget(sessionId);
            _ = children.AuthorizeDirectChild(child.SessionId);
            var activity = child.Activity.Capture();
            return Task.FromResult<ToolExecutionResult>(Format(child, activity));
        }
        catch (AgentRegistryException failure)
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, failure.Message));
        }
    }

    private static void AppendActivity(StringBuilder report, AgentSessionActivitySnapshot activity)
    {
        _ = report.Append("\nRequest session duration: ")
            .Append(activity.RequestSessionDuration is { } requestDuration
                ? FormatDuration(requestDuration)
                : "none");

        if (activity.CurrentProviderRequestDuration is { } currentProviderDuration)
        {
            _ = report.Append("\nCurrent provider request duration: ")
                .Append(FormatDuration(currentProviderDuration));
        }
        else if (activity.LastProviderRequestDuration is { } lastProviderDuration)
        {
            _ = report.Append("\nLast provider request duration: ")
                .Append(FormatDuration(lastProviderDuration));
        }
        else
        {
            _ = report.Append("\nProvider request duration: none");
        }

        if (activity.CurrentTool is { } tool)
        {
            _ = report.Append("\nCurrent tool: ").Append(tool);
        }
        else if (activity.LatestProviderActivityAge is { } age)
        {
            _ = report.Append("\nLatest provider activity: ").Append(FormatDuration(age)).Append(" ago");
        }
        else
        {
            _ = report.Append("\nCurrent activity: none");
        }

        if (activity.TerminalOutcome is { } outcome)
        {
            _ = report.Append("\nLast outcome: ").Append(outcome.Status.ToString().ToLowerInvariant());
        }
    }

    private static void AppendRecent(StringBuilder report, AgentSessionActivitySnapshot activity)
    {
        _ = report.Append("\nRecent entries:");
        if (activity.Recent.Count == 0)
        {
            _ = report.Append(" none");
            return;
        }

        foreach (var entry in activity.Recent)
        {
            var content = entry.Content.Replace("\n", "\n  ", StringComparison.Ordinal);
            _ = report.Append("\n- ")
                .Append(entry.Kind == AgentSessionActivityEntryKind.ReasoningSummary
                    ? "reasoning summary"
                    : "assistant message")
                .Append(" (").Append(FormatDuration(entry.Age)).Append(" ago): ")
                .Append(content);
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:F1}s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMilliseconds:F0}ms");
    }

    private string Format(AgentSession child, AgentSessionActivitySnapshot activity)
    {
        var report = new StringBuilder("Agent status");
        _ = report.Append("\nSession: ").Append(child.SessionId);
        _ = report.Append("\nName: ").Append(child.Name);
        _ = report.Append("\nLifecycle: ").Append(activity.State.ToString().ToLowerInvariant());
        AppendActivity(report, activity);
        AppendActive(report, child.SessionId);
        AppendRecent(report, activity);
        return report.ToString();
    }

    private void AppendActive(StringBuilder report, string childSessionId)
    {
        var activeChildren = children.AuthorizeDirectChild(childSessionId).ChildRegistry.ObserveActive();
        _ = report.Append("\nActive direct subagents:");
        if (activeChildren.Count == 0)
        {
            _ = report.Append(" none");
        }
        else
        {
            foreach (var child in activeChildren)
            {
                _ = report.Append("\n- ").Append(child.Name).Append(" (").Append(child.Id).Append(')');
            }
        }

        var activeProcesses = processes.Snapshot()
            .Where(process => string.Equals(process.OwnerSessionId, childSessionId, StringComparison.Ordinal));
        _ = report.Append("\nActive processes:");
        var count = 0;
        foreach (var process in activeProcesses)
        {
            _ = report.Append("\n- ").Append(process.Name).Append(" (").Append(process.Id).Append(", shell, running)");
            count++;
        }

        if (count == 0)
        {
            _ = report.Append(" none");
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }
    }
}
