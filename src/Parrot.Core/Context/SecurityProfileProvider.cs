using Parrot.Agent;
using Parrot.Config;
using Parrot.Process;
using Parrot.Security;

namespace Parrot.Context;

internal sealed class SecurityProfileProvider(
    IReadOnlyList<SandboxRule> rules,
    SandboxGate sandboxGate,
    IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    private readonly SandboxRule[] _rules = [.. rules];

    public string Key => "runtime:system-context:16-security-profile";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new SecurityProfilePrompt(_rules, sandboxGate.Enabled, templates);
    }
}
