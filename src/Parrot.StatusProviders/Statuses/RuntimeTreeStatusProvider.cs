using System.Text.Json;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Process;
using Parrot.Queues;
using Scriban.Runtime;

namespace Parrot.Statuses;

internal sealed class RuntimeTreeStatusProvider(
    IAgentRegistry agents,
    IPromptTemplateCatalog templates) : IStatusProvider
{
    public string Key => "runtime:queues";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var scopes = agents.SnapshotScopes();
        var queueOwners = scopes.Select(static scope => scope.GetService<IAgentQueues>().Snapshot()).ToArray();
        var activeAgents = agents.ActiveSnapshot();
        var activeProcesses = scopes.SelectMany(static scope => scope.GetService<IProcessOwner>().Snapshot()).ToArray();
        var nodes = BuildNodes(query.SessionId, queueOwners, activeAgents, activeProcesses);
        foreach (var scope in scopes)
        {
            if (nodes.TryGetValue(scope.Session.SessionId, out var node))
            {
                var state = scope.Session.Activity.Capture().State;
                node.Status = state == DrainState.Idle ? string.Empty : state.ToString().ToLowerInvariant();
            }
        }

        var agentModels = new ScriptArray();
        Append(agentModels, nodes, query.SessionId, 0, new HashSet<string>(StringComparer.Ordinal));
        var model = new ScriptObject
        {
            ["section"] = "tree",
            ["agents"] = agentModels,
            ["runs"] = new ScriptArray(),
        };
        return ValueTask.FromResult(StatusObservation.AvailableText(
            templates.RenderStructured("status.runtime", model, cancellationToken)));
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

    private static void Append(ScriptArray agentModels, Dictionary<string, Node> nodes, string sessionId, int depth, HashSet<string> seen)
    {
        if (!seen.Add(sessionId) || !nodes.TryGetValue(sessionId, out var node))
        {
            return;
        }

        var queues = new ScriptArray();
        foreach (var queue in node.Queues.OrderBy(queue => queue.Name, StringComparer.Ordinal))
        {
            queues.Add(new ScriptObject
            {
                ["name"] = queue.Name,
                ["size"] = queue.Size.ToString(System.Globalization.CultureInfo.CurrentCulture),
                ["description"] = string.IsNullOrEmpty(queue.Description)
                    ? string.Empty
                    : JsonSerializer.Serialize(queue.Description, StatusJsonContext.Default.String),
            });
        }

        var processes = new ScriptArray();
        foreach (var process in node.Processes.OrderBy(process => process.ProcessId, StringComparer.Ordinal))
        {
            processes.Add(new ScriptObject
            {
                ["id"] = process.Id,
                ["state"] = State(process.State),
                ["name"] = process.Name,
            });
        }

        agentModels.Add(new ScriptObject
        {
            ["indent"] = new string(' ', depth * 2),
            ["name"] = node.Name,
            ["status"] = node.Status,
            ["session_id"] = node.SessionId,
            ["queues"] = queues,
            ["processes"] = processes,
        });
        foreach (var child in node.Children.OrderBy(child => child.SessionId, StringComparer.Ordinal))
        {
            Append(agentModels, nodes, child.SessionId, depth + 1, seen);
        }
    }

    private sealed class Node(string sessionId, string parentSessionId, string name)
    {
        public string SessionId { get; } = sessionId;

        public string ParentSessionId { get; set; } = parentSessionId;

        public string Name { get; set; } = name;

        public string Status { get; set; } = string.Empty;

        public List<QueueInfo> Queues { get; } = [];

        public List<ShellProcessStatusSnapshot> Processes { get; } = [];

        public List<Node> Children { get; } = [];
    }
}
