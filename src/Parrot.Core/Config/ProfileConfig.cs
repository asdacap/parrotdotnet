using Parrot.Security;

namespace Parrot.Config;

internal sealed record ProfileConfig(
    string Prompt,
    string HardRule,
    string Status,
    int MaxToolRounds,
    bool ReadOnly,
    IReadOnlyList<SandboxRule> SandboxRules);
