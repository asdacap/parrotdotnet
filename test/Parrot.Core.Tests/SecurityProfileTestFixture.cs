using Parrot.Agent;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class SecurityProfileTestFixture
{
    private static readonly ProjectWorkspace Workspace = ProjectWorkspace.FromLaunchDirectory(Directory.GetCurrentDirectory());

    public SecurityProfileTestFixture(SecurityProfile policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Security = new AgentSessionSecurity(policy, Workspace, Workspace.LaunchDirectory);
    }

    public AgentSessionSecurity Security { get; }
}
