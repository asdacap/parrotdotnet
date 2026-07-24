using Parrot.Context;
using Parrot.Llm;
using Parrot.Process;

namespace Parrot.Agent;

internal sealed class AgentSessionFactorySource(
    ILLMProvider provider,
    string workingDirectory,
    ProcessRunner processes,
    SystemContextBuilder systemContext,
    Compactor compactor) : IAgentSessionFactorySource
{
    public IAgentSessionFactory Create(UserSession owner) =>
        new AgentSessionFactory(owner, provider, workingDirectory, processes, systemContext, compactor);
}
