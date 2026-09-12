using Parrot.Agent;
using Parrot.Config;
using Parrot.Security;

namespace Parrot.Context;

internal sealed class SecurityProfileProvider(IReadOnlyList<SandboxRule> rules, IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    private readonly SandboxRule[] _rules = [.. rules];

    public string Key => "runtime:system-context:16-security-profile";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new SecurityProfilePrompt(_rules, templates);
    }
}
