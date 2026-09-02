using System.Text.Json;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Process;
using Parrot.Queues;

namespace Parrot.Statuses;

internal sealed class RuntimeTreeStatusProvider(
    AgentQueueCatalog queues,
    IProcessStatusSource processes,
    IAgentStatusSource agents,
    PromptTemplateCatalog templates) : IStatusProvider
{
    public string Key => "runtime:queues";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var queueOwners = queues.Snapshot();
        var activeAgents = agents.ActiveSnapshot();
        var activeProcesses = processes.Snapshot();
        var nodes = BuildNodes(query.SessionId, queueOwners, activeAgents, activeProcesses);
        var lines = new List<string> { templates.Render("status.runtime", []) };
        Append(lines, nodes, query.SessionId, 0, new HashSet<string>(StringComparer.Ordinal));
        return ValueTask.FromResult(StatusObservation.AvailableText(string.Join('\n', lines)));
    }

    private static Dictionary<string, Node> BuildNodes(
        string rootSessionId,
        IReadOnlyList<QueueOwnerSnapshot> queueOwners,
        IReadOnlyList<ActiveAgentSnapshot> activeAgents,
        IReadOnlyList<ShellProcessStatusSnapshot> activeProcesses)
    {
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal)
        {
            [rootSessionId] = new(rootSessionId, string.Empty, rootSessionId),
        };

        foreach (var owner in queueOwners)
        {
            Add(nodes, owner.Identity.SessionId, owner.Identity.ParentSessionId, owner.Identity.Name)
                .Queues.AddRange(owner.Queues);
        }

        foreach (var agent in activeAgents)
        {
            _ = Add(nodes, agent.SessionId, agent.ParentSessionId, agent.Name);
        }

        foreach (var process in activeProcesses)
        {
            Add(nodes, process.OwnerSessionId, string.Empty, process.OwnerSessionId).Processes.Add(process);
        }

        foreach (var node in nodes.Values)
        {
            if (node.SessionId == rootSessionId || node.ParentSessionId.Length == 0)
            {
                continue;
            }

            if (nodes.TryGetValue(node.ParentSessionId, out var parent))
            {
                parent.Children.Add(node);
            }
        }

        return nodes;
    }

    private static Node Add(Dictionary<string, Node> nodes, string sessionId, string parentSessionId, string name)
    {
        if (nodes.TryGetValue(sessionId, out var existing))
        {
            if (existing.ParentSessionId.Length == 0 && parentSessionId.Length > 0)
            {
                existing.ParentSessionId = parentSessionId;
            }

            if (existing.Name == existing.SessionId && name.Length > 0)
            {
                existing.Name = name;
            }

            return existing;
        }

        var created = new Node(sessionId, parentSessionId, name.Length == 0 ? sessionId : name);
        nodes.Add(sessionId, created);
        return created;
    }

    private static string State(ActiveWorkState state) => state switch
    {
        ActiveWorkState.Running => "running",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown active work state."),
    };

    private void Append(List<string> lines, Dictionary<string, Node> nodes, string sessionId, int depth, HashSet<string> seen)
    {
        if (!seen.Add(sessionId) || !nodes.TryGetValue(sessionId, out var node))
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        lines.Add(templates.Render("status.runtime.agent", [
            new PromptTemplateArgument("indent", indent),
            new PromptTemplateArgument("name", node.Name),
            new PromptTemplateArgument("session_id", node.SessionId),
        ]));
        foreach (var queue in node.Queues.OrderBy(queue => queue.Name, StringComparer.Ordinal))
        {
            var description = string.IsNullOrEmpty(queue.Description)
                ? string.Empty
                : templates.Render("status.runtime.queue-description", [
                    new PromptTemplateArgument(
                        "description",
                        JsonSerializer.Serialize(queue.Description, StatusJsonContext.Default.String)),
                ]);
            lines.Add(templates.Render("status.runtime.queue", [
                new PromptTemplateArgument("indent", indent),
                new PromptTemplateArgument("name", queue.Name),
                new PromptTemplateArgument("size", queue.Size.ToString(System.Globalization.CultureInfo.CurrentCulture)),
                new PromptTemplateArgument("description", description),
            ]));
        }

        foreach (var process in node.Processes.OrderBy(process => process.ProcessId, StringComparer.Ordinal))
        {
            lines.Add(templates.Render("status.runtime.process", [
                new PromptTemplateArgument("indent", indent),
                new PromptTemplateArgument("id", process.Id),
                new PromptTemplateArgument("state", State(process.State)),
                new PromptTemplateArgument("name", process.Name),
            ]));
        }

        foreach (var child in node.Children.OrderBy(child => child.SessionId, StringComparer.Ordinal))
        {
            Append(lines, nodes, child.SessionId, depth + 1, seen);
        }
    }

    private sealed class Node(string sessionId, string parentSessionId, string name)
    {
        public string SessionId { get; } = sessionId;

        public string ParentSessionId { get; set; } = parentSessionId;

        public string Name { get; set; } = name;

        public List<QueueInfo> Queues { get; } = [];

        public List<ShellProcessStatusSnapshot> Processes { get; } = [];

        public List<Node> Children { get; } = [];
    }
}
