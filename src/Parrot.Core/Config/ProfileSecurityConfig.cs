using Parrot.Security;

namespace Parrot.Config;

internal sealed class ProfileSecurityConfig
{
    public bool? ReadOnly { get; init; }

    public IReadOnlyList<SandboxRule> SandboxRules { get; init; } = [];
}
