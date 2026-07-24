using Parrot.Context;
using Parrot.Llm;
using Parrot.Process;

namespace Parrot.Agent;

internal sealed class AgentSessionFactorySource(
    string workingDirectory,
    ProcessRunner processes,
    SystemContextBuilder systemContext,
    Compactor compactor) : IAgentSessionFactorySource
{
    public IAgentSessionFactory Create(UserSession owner, ILLMProvider provider) =>
        new AgentSessionFactory(owner, workingDirectory, processes, systemContext, compactor);
}
