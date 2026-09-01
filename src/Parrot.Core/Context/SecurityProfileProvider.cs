using Parrot.Agent;
using Parrot.Security;

namespace Parrot.Context;

internal sealed class SecurityProfileProvider(IReadOnlyList<SandboxRule> rules) : ISystemPromptProvider
{
    private readonly SandboxRule[] _rules = [.. rules];

    public string Key => "runtime:system-context:10-security-profile";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new SecurityProfilePrompt(_rules);
    }
}
