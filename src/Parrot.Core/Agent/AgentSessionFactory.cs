using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Agent;

internal sealed class AgentSessionFactory(
    ILLMProvider provider,
    ToolRegistry tools,
    string workingDirectory,
    ProcessRunner processes,
    SystemContextBuilder systemContext,
    Compactor compactor) : IAgentSessionFactory
{
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
            tools,
            workingDirectory,
            processes,
            systemContext,
            compactor,
            depth);
}
