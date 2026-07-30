namespace Parrot.Permissions;

internal sealed record PermissionPending(
    string Id,
    string AgentSessionId,
    string Reason,
    IReadOnlyList<SandboxWriteTarget> Targets,
    IReadOnlyList<PermissionChoice> Choices);
