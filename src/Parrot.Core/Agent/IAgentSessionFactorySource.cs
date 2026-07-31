using Parrot.Process;
using Parrot.Queues;

namespace Parrot.Agent;

// Mints the per-user-session agent factory. The seam exists because the static
// half of a session is composed in Parrot.Cli, while the UserSession a factory
// is scoped to only exists inside that user session's own constructor.
internal interface IAgentSessionFactorySource
{
    IAgentSessionFactory Create(UserSession owner);

    ShellProcessOwners CreateShellProcesses(UserSession owner);

    AgentQueueCatalog CreateQueueCatalog(UserSession owner);
}
