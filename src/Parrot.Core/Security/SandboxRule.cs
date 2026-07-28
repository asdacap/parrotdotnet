namespace Parrot.Security;

internal sealed record SandboxRule(string Path, SandboxRuleAction Action);
