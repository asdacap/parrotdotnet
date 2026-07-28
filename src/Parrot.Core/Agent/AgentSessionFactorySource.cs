using Parrot.Context;
using Parrot.Process;
using Parrot.Store;
using Parrot.Web;

namespace Parrot.Agent;

internal sealed class AgentSessionFactorySource(
    string workingDirectory,
    string configDirectory,
    SessionIndex sessionIndex,
    ProcessRunner processes,
    string date,
    Compactor compactor,
    WebFetcher webFetcher) : IAgentSessionFactorySource
{
    public IAgentSessionFactory Create(UserSession owner) =>
        new AgentSessionFactory(owner, workingDirectory, configDirectory, date, compactor, webFetcher);

    public ShellProcessOwner CreateShellProcesses(UserSession owner) =>
        new(
            workingDirectory,
            sessionIndex.BlobDirectoryFor(owner.Id),
            processes,
            owner.Lifetime);
}
