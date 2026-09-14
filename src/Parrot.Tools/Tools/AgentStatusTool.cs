using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Process;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class AgentStatusTool(
    IAgentResolver resolver,
    IAgentParentScope parentScope) : ITool
{
    public string Name => "agent_status";

    public bool IsEnabledAfterInterruption => true;

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
            var childScope = resolver.ResolveStatusTargetScope(sessionId);
            _ = parentScope.AuthorizeDirectChild(childScope.Session.SessionId);
            var activity = childScope.Session.Activity.Capture();
            return Task.FromResult<ToolExecutionResult>(Format(childScope, activity));
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

    private static string Format(IAgentSessionScope childScope, AgentSessionActivitySnapshot activity)
    {
        var report = new StringBuilder("Agent status");
        _ = report.Append("\nName: ").Append(childScope.Session.Name);
        _ = report.Append("\nLifecycle: ").Append(activity.State.ToString().ToLowerInvariant());
        AppendActivity(report, activity);
        AppendActive(report, childScope);
        AppendRecent(report, activity);
        return report.ToString();
    }

    private static void AppendActive(StringBuilder report, IAgentSessionScope childScope)
    {
        var activeChildren = childScope.ChildRegistry.SnapshotDescendants()
            .Where(session => session.IsActive()
                && string.Equals(session.ParentSessionId, childScope.Session.SessionId, StringComparison.Ordinal))
            .Select(static session => new ActiveWorkObservation(
                session.SessionId,
                session.Name,
                ActiveWorkKind.Agent,
                ActiveWorkState.Running))
            .OrderBy(static observation => observation.Id, StringComparer.Ordinal)
            .ToArray();
        _ = report.Append("\nActive direct subagents:");
        if (activeChildren.Length == 0)
        {
            _ = report.Append(" none");
        }
        else
        {
            foreach (var child in activeChildren)
            {
                _ = report.Append("\n- ").Append(child.Name);
            }
        }

        var activeProcesses = childScope.GetService<IProcessOwner>().Snapshot();
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
