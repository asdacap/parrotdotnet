using Parrot.Security;

namespace Parrot.Config;

internal sealed record ProfileConfig(
    string Prompt,
    string Usage,
    IReadOnlyList<string> HardRules,
    IReadOnlyList<string>? AllowedTools,
    int MaxTurns,
    int RecursionLimit,
    bool ReadOnly,
    bool IsUserAgent,
    IReadOnlyList<SandboxRule> SandboxRules);
