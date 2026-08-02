using Parrot.Agent;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal static class SecurityProfileTestFactory
{
    public static AgentSessionSecurity Create(SecurityProfile policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var workspace = ProjectWorkspace.FromLaunchDirectory(Directory.GetCurrentDirectory());
        var scratch = new AgentScratchDirectory(Path.Combine(
            workspace.LaunchDirectory,
            ".test-agent-scratch",
            Guid.NewGuid().ToString("n")));
        return new AgentSessionSecurity(policy, workspace, scratch.Root);
    }
}
