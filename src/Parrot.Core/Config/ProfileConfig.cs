using Parrot.Security;

namespace Parrot.Config;

internal sealed record ProfileConfig(
    string Prompt,
    string HardRule,
    int MaxToolRounds,
    bool ReadOnly,
    IReadOnlyList<SandboxRule> SandboxRules);
