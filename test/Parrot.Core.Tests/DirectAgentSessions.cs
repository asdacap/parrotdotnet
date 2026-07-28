using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
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

    public IReadOnlyList<AgentIdentity> Identities => _identities;

    public IAgentSessionFactory Create(UserSession owner) => this;

    public ShellProcessOwner CreateShellProcesses(UserSession owner) =>
        new(".", ".", new ProcessRunner(string.Empty), owner.Lifetime);

    public AgentSession Create(
        AgentIdentity identity,
        ProviderModel model,
        EventBroker eventBroker,
        EventRepository eventRepository,
        ModeProfile? mode,
        SecurityProfile securityProfile,
        RuntimeStatus? status,
        CancellationToken lifetime)
    {
        _identities.Add(identity);
        return new AgentSession(
            identity,
            model,
            eventBroker,
            eventRepository,
            [],
            new SystemContextBuilder(".", "2026-07-24", identity.Context),
            new Compactor(120_000),
            mode,
            securityProfile,
            status,
            lifetime);
    }
}
