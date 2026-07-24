using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Agent;

// Per user session, which is what lets it hold that session's tool factories:
// a factory is constructed with the owner it belongs to, and yields one tool
// instance per agent session from there.
internal sealed class AgentSessionFactory(
    UserSession owner,
    ILLMProvider provider,
    string workingDirectory,
    ProcessRunner processes,
    SystemContextBuilder systemContext,
    Compactor compactor) : IAgentSessionFactory
{
    private readonly IReadOnlyList<IToolFactory> _toolFactories =
    [
        new ExecCommandToolFactory(workingDirectory, processes),
        new ReadFileToolFactory(workingDirectory),
        new AgentSpawnToolFactory(owner),
    ];

    public AgentSession Create(
        string sessionId,
        int depth,
        EventBroker eventBroker,
        EventRepository eventRepository) =>
        new(
            sessionId,
            provider,
            eventBroker,
            eventRepository,
            _toolFactories,
            systemContext,
            compactor,
            depth);
}
