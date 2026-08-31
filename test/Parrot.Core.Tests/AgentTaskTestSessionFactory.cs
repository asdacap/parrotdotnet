using System.Collections.Concurrent;
using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Security;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskTestSessionFactory(ModelRouter router) : IAgentSessionFactory
{
    private static readonly ConcurrentBag<ShellProcessOwners> ProcessOwners = [];
    private static readonly ConcurrentBag<AgentQueueCatalog> QueueCatalogs = [];
    private readonly List<AgentIdentity> _identities = [];

    internal IReadOnlyList<AgentIdentity> Identities => _identities;

    public IAgentSessionLease Create(
        AgentIdentity identity,
        ModelSelector model,
        EventBroker eventBroker,
        EventRepository eventRepository,
        IMode mode,
        SecurityProfile securityProfile,
        RuntimeStatus status,
        AgentRegistry registry,
        CancellationToken lifetime)
    {
        _identities.Add(identity);
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "parrot-agent-task-tests", Guid.NewGuid().ToString("N"))).FullName;
        var resources = new UserSessionResources(
            new StatePaths(root, root, root),
            UserSessionId.Parse(Guid.NewGuid().ToString("N")),
            ProjectWorkspace.FromLaunchDirectory(root));
        var owners = new ShellProcessOwners(resources, new ProcessRunner(string.Empty), lifetime);
        ProcessOwners.Add(owners);
        var processes = owners.Prepare(identity.SessionId);
        owners.Register(processes);
        var queues = new AgentQueueCatalog(resources);
        QueueCatalogs.Add(queues);
        if (identity.ParentSessionId.Length > 0)
        {
            _ = queues.Register(AgentIdentity.Main(identity.ParentSessionId, identity.ParentSessionName));
        }

        var agentQueues = queues.Register(identity);
        var session = new AgentSession(
            identity,
            model,
            router,
            eventBroker,
            eventRepository,
            [],
            TestModels.EmptyToolDefinitions,
            TestModels.MaterializePrompt(identity, root, root),
            new TodoCollection(identity.SessionId, eventRepository, eventBroker),
            new ToolOutputBlobStore(root),
            new Parrot.Context.Compactor(90, 30, 60_000, 1024),
            new ActiveWorkCompletionReminder(identity.SessionId, registry, processes),
            mode,
            SecurityProfileTestFactory.Create(securityProfile),
            status,
            registry,
            agentQueues,
            lifetime);
        agentQueues.Attach(session);
        return new AgentSessionLease(session);
    }
}
