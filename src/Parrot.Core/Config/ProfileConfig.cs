using Parrot.Security;

namespace Parrot.Config;

internal sealed record ProfileConfig(
    string Prompt,
    string Usage,
    IReadOnlyList<string>? AllowedTools,
    int MaxTurns,
    int RecursionLimit,
    bool ReadOnly,
    bool EnforceActiveWorkCompletion,
    IReadOnlyList<SandboxRule> SandboxRules);
