using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Store;

namespace Parrot.Core.Tests;

// Builds real agent sessions with no tools. The production factory is composed
// around a ProcessRunner and a sandbox, which a test of the wire contract has
// no use for -- but the session itself has to be real, because the drain is
// what the contract is a contract over.
internal sealed class DirectAgentSessions : IAgentSessionFactorySource, IAgentSessionFactory
{
    public IAgentSessionFactory Create(UserSession owner, ILLMProvider provider) => this;

    public ShellProcessOwner CreateShellProcesses(UserSession owner) =>
        new(".", ".", new ProcessRunner(string.Empty), owner.Lifetime);

    public AgentSession Create(
        string sessionId,
        ILLMProvider provider,
        string model,
        int depth,
        EventBroker eventBroker,
        EventRepository eventRepository,
        CancellationToken lifetime) =>
        new(
            sessionId,
            provider,
            eventBroker,
            eventRepository,
            [],
            new SystemContextBuilder(".", "2026-07-24"),
            new Compactor(120_000),
            depth,
            lifetime)
        {
            Model = model,
        };
}
