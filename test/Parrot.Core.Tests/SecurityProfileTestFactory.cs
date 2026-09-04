using Parrot.Agent;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal static class SecurityProfileTestFactory
{
    private static readonly ProjectWorkspace Workspace = ProjectWorkspace.FromLaunchDirectory(Directory.GetCurrentDirectory());

    public static AgentSessionSecurity Create(SecurityProfile policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new AgentSessionSecurity(policy, Workspace, Workspace.LaunchDirectory);
    }
}
