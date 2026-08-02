using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

// Builds real agent sessions with no tools. The production factory is composed
// around a ProcessRunner and a sandbox, which a test of the wire contract has
// no use for -- but the session itself has to be real, because the drain is
// what the contract is a contract over.
internal sealed class DirectAgentSessions : IAgentSessionFactorySource
{
    private readonly List<AgentIdentity> _identities = [];
    private readonly List<AgentSession> _sessions = [];
    private readonly List<UserSession> _owners = [];
    private ModelRouter? _router;
    private bool _includeStatusTool;

    public IReadOnlyList<AgentIdentity> Identities => _identities;

    public IReadOnlyList<AgentSession> Sessions => _sessions;

    public IReadOnlyList<UserSession> Owners => _owners;

    public void IncludeStatusTool() => _includeStatusTool = true;

    public void Use(ModelRouter router) => _router = router;

    public IAgentSessionFactory Create(UserSession owner)
    {
        _owners.Add(owner);
        return new OwnerAgentSessions(this, owner);
    }

    public ShellProcessOwners CreateShellProcesses(UserSession owner) =>
        new(owner.Resources, new ProcessRunner(string.Empty), owner.Lifetime);

    public AgentQueueCatalog CreateQueueCatalog(UserSession owner) =>
        new(owner.Resources);

    private sealed class OwnerAgentSessions(DirectAgentSessions source, UserSession owner) : IAgentSessionFactory
    {
        public IAgentSessionLease Create(
            AgentIdentity identity,
            ModelSelector model,
            EventBroker eventBroker,
            EventRepository eventRepository,
            IAgentProfile profile,
            SecurityProfile securityProfile,
            RuntimeStatus status,
            AgentRegistry registry,
            CancellationToken lifetime)
        {
            source._identities.Add(identity);
            var processes = owner.ShellProcesses.Prepare(identity.SessionId);
            owner.ShellProcesses.Register(processes);
            var router = source._router ?? throw new InvalidOperationException("model router is not configured");
            var queues = owner.QueueCatalog.Register(identity);
            var session = new AgentSession(
                identity,
                model,
                router,
                eventBroker,
                eventRepository,
                source._includeStatusTool ? [new StatusToolFactory(owner.Status)] : [],
                TestModels.MaterializePrompt(identity, ".", "."),
                new TodoCollection(identity.SessionId, eventRepository, eventBroker),
                new ToolOutputBlobStore(Path.GetTempPath()),
                new Compactor(90, 30, 60_000, 1024),
                new ActiveWorkCompletionReminder(identity.SessionId, registry, processes),
                profile,
                SecurityProfileTestFactory.Create(securityProfile),
                status,
                registry,
                queues,
                lifetime);
            queues.Attach(session);
            source._sessions.Add(session);
            return new AgentSessionLease(session);
        }
    }
}
