using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

// Builds real agent sessions with no tools. The production factory is composed
// around a ProcessRunner and a sandbox, which a test of the wire contract has
// no use for -- but the session itself has to be real, because the drain is
// what the contract is a contract over.
internal sealed class DirectAgentSessions : IAgentSessionFactorySource, IAgentSessionFactory
{
    private readonly List<AgentIdentity> _identities = [];
    private ModelRouter? _router;
    private UserSession? _owner;

    public IReadOnlyList<AgentIdentity> Identities => _identities;

    public void Use(ModelRouter router) => _router = router;

    public IAgentSessionFactory Create(UserSession owner)
    {
        _owner = owner;
        return this;
    }

    public ShellProcessOwner CreateShellProcesses(UserSession owner) =>
        new(".", ".", new ProcessRunner(string.Empty), owner.Lifetime);

    public QueueStore CreateQueues(UserSession owner) =>
        new(Path.Combine(Path.GetTempPath(), "parrot-tests", owner.Id, "queues"));

    public IAgentSessionLease Create(
        AgentIdentity identity,
        ModelSelector model,
        EventBroker eventBroker,
        EventRepository eventRepository,
        MainAgentProfile? profile,
        SecurityProfile securityProfile,
        RuntimeStatus? status,
        AgentRegistry registry,
        CancellationToken lifetime)
    {
        _identities.Add(identity);
        var router = _router ?? throw new InvalidOperationException("model router is not configured");
        return new AgentSessionLease(new AgentSession(
            identity,
            model,
            router,
            eventBroker,
            eventRepository,
            [],
            TestModels.PromptProvider(".", "."),
            new TodoCollection(identity.SessionId, eventRepository, eventBroker),
            new ToolOutputBlobStore(Path.GetTempPath()),
            new Compactor(120_000),
            profile,
            securityProfile,
            status,
            registry,
            _owner,
            lifetime));
    }
}
