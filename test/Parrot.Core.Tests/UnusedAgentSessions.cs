using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Store;

namespace Parrot.Core.Tests;

// A user session builds its factory eagerly and its main agent session lazily,
// so a test that only needs the user session as a tool's owner must supply a
// source that works and a factory that is never reached.
internal sealed class UnusedAgentSessions : IAgentSessionFactorySource, IAgentSessionFactory
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
        throw new NotSupportedException();
}
