namespace Parrot.Security;

internal sealed record MaterializedSandboxRule(string Path, bool Read, bool Write);
